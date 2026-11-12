namespace CouponHubBot.Services

open System
open System.Threading.Tasks
open CouponHubBot.Telemetry
open CouponHubBot.Utils
open BotInfra
open Funogram.Telegram.Types
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options

type TelegramMembershipService(
    db: DbService,
    tg: ITelegramApi,
    options: IOptions<CouponHubBot.BotConfiguration>,
    logger: ILogger<TelegramMembershipService>,
    time: TimeProvider
) =
    let isMemberOfChat (cm: ChatMember) =
        match cm with
        | ChatMember.Owner _ | ChatMember.Administrator _ | ChatMember.Member _ -> true
        | ChatMember.Restricted _ | ChatMember.Left _ | ChatMember.Banned _ -> false

    let rec checkMembership (chatId: int64) userId maxAge =
        task {
            let! entry = db.GetMembershipEntry(chatId, userId)
            let now = time.GetUtcNow().UtcDateTime
            if entry.is_member.HasValue && now - entry.checked_at < maxAge then
                return entry.is_member.Value
            else
                let! cm = tg.CallExn(Funogram.Telegram.Req.GetChatMember.Make(chatId, userId))
                let isMember = isMemberOfChat cm
                // A concurrent membership change or refresh invalidates this lookup's revision.
                let! saved = db.TryCacheMembership(chatId, userId, entry.revision, isMember, now)
                if saved then return isMember
                else return! checkMembership chatId userId maxAge
        }

    static member MaxCacheAge = TimeSpan.FromHours 1.0

    member _.OnChatMemberUpdated(update: ChatMemberUpdated) =
        task {
            use span = botActivity.StartActivity("onChatMemberUpdated")
            if update.Chat.Id = options.Value.CommunityChatId then
                let uid = (Tg.chatMemberUser update.NewChatMember).Id
                let status = Tg.chatMemberStatus update.NewChatMember
                let isMember = isMemberOfChat update.NewChatMember
                do! db.InvalidateMembership(update.Chat.Id, uid)
                %span.SetTag("userId", uid)
                %span.SetTag("isMember", isMember)
                %span.SetTag("status", status)
                if isMember then
                    logger.LogInformation("Community member added: user {UserId} (status {Status})", uid, status)
                else
                    logger.LogInformation("Community member removed: user {UserId} (status {Status})", uid, status)
                return Some isMember
            else
                return None
        }

    member _.IsMember(userId, maxAge: TimeSpan) =
        task {
            try
                return! checkMembership options.Value.CommunityChatId userId maxAge
            with ex ->
                logger.LogWarning(ex, "Failed to check membership for {UserId}", userId)
                return false
        }

    member this.IsMember(userId) = this.IsMember(userId, TelegramMembershipService.MaxCacheAge)

type MembershipCacheCleanupService(db: DbService, logger: ILogger<MembershipCacheCleanupService>) =
    inherit BackgroundService()

    override _.ExecuteAsync(stoppingToken) =
        task {
            while not stoppingToken.IsCancellationRequested do
                try
                    do! db.PruneMembershipCache(TelegramMembershipService.MaxCacheAge)
                with ex ->
                    logger.LogWarning(ex, "Failed to prune expired membership entries")
                do! Task.Delay(TimeSpan.FromDays(1.0), stoppingToken)
        }
