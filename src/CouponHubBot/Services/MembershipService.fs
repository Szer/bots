namespace CouponHubBot.Services

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open CouponHubBot.Telemetry
open CouponHubBot.Utils
open BotInfra
open Funogram.Telegram.Types
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options

type TelegramMembershipService(
    tg: ITelegramApi,
    options: IOptions<CouponHubBot.BotConfiguration>,
    logger: ILogger<TelegramMembershipService>,
    time: TimeProvider
) =
    // userId -> (isMember, cachedAtUtc)
    let cache = ConcurrentDictionary<int64, bool * DateTime>()
    let expiry = TimeSpan.FromDays(1.0)

    let isFresh (cachedAt: DateTime) =
        time.GetUtcNow().UtcDateTime - cachedAt < expiry

    let isMemberOfChat (cm: ChatMember) =
        match cm with
        | ChatMember.Owner _ | ChatMember.Administrator _ | ChatMember.Member _ -> true
        | ChatMember.Restricted _ | ChatMember.Left _ | ChatMember.Banned _ -> false

    member _.InvalidateCache() = cache.Clear()

    member _.OnChatMemberUpdated(update: ChatMemberUpdated) =
        use span =
            botActivity
                .StartActivity("onChatMemberUpdated")
        if update.Chat.Id = options.Value.CommunityChatId then
            let uid = (Tg.chatMemberUser update.NewChatMember).Id
            let status = Tg.chatMemberStatus update.NewChatMember
            let isMember = isMemberOfChat update.NewChatMember
            cache[uid] <- (isMember, time.GetUtcNow().UtcDateTime)
            %span.SetTag("userId", uid)
            %span.SetTag("isMember", isMember)
            %span.SetTag("status", status)

    member _.IsMember(userId) =
        task {
            match cache.TryGetValue(userId) with
            | true, (isMember, cachedAt) when isFresh cachedAt -> return isMember
            | _ ->
                try
                    let! cm = tg.CallExn(Funogram.Telegram.Req.GetChatMember.Make(options.Value.CommunityChatId, userId))
                    let isMember = isMemberOfChat cm
                    cache[userId] <- (isMember, time.GetUtcNow().UtcDateTime)
                    return isMember
                with ex ->
                    logger.LogWarning(ex, "Failed to check membership for {UserId}", userId)
                    return false
        }

/// Clears membership cache on startup and then once per day (insurance against missed updates).
type MembershipCacheInvalidationService(membership: TelegramMembershipService, logger: ILogger<MembershipCacheInvalidationService>) =
    inherit BackgroundService()

    override _.ExecuteAsync(stoppingToken) =
        task {
            membership.InvalidateCache()
            logger.LogInformation("Membership cache invalidated on startup")

            while not stoppingToken.IsCancellationRequested do
                do! Task.Delay(TimeSpan.FromDays(1.0), stoppingToken)
                if not stoppingToken.IsCancellationRequested then
                    membership.InvalidateCache()
                    logger.LogInformation("Membership cache invalidated (daily)")
        }
