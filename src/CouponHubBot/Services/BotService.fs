namespace CouponHubBot.Services

open System
open System.Collections.Generic
open System.Diagnostics
open System.Runtime.ExceptionServices
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Funogram.Telegram.Types
open CouponHubBot
open CouponHubBot.Services
open CouponHubBot.Telemetry
open CouponHubBot.Utils
open BotInfra

type BotService(
    tg: ITelegramApi,
    options: IOptions<BotConfiguration>,
    db: DbService,
    membership: TelegramMembershipService,
    couponFlow: CouponFlowHandler,
    commandHandler: CommandHandler,
    callbackHandler: CallbackHandler,
    gitHub: GitHubService,
    logger: ILogger<BotService>,
    time: TimeProvider
) =
    let sendText = BotHelpers.sendText tg
    let ensureCommunityMember = BotHelpers.ensureCommunityMember membership sendText

    let handleCommunityMessage (msg: Message) =
        task {
            let botConfig = options.Value
            if msg.Chat.Id = botConfig.CommunityChatId then
                let hasPhoto = msg.Photo |> Option.exists (fun p -> p.Length > 0)
                let hasDocument = msg.Document.IsSome
                // Only persist regular content messages (text/photo/document); Telegram
                // service/system messages have none of these fields set.
                let isRegularContent = msg.Text.IsSome || hasPhoto || hasDocument

                if isRegularContent then
                    // Determine sender: regular user (msg.From) or anonymous admin/channel post (msg.SenderChat)
                    let senderId =
                        match msg.From with
                        | Some u when not u.IsBot -> Some u.Id
                        | _ -> msg.SenderChat |> Option.map (fun c -> c.Id)
                    match senderId with
                    | None -> ()
                    | Some userId ->
                        let text = msg.Text |> Option.orElse msg.Caption |> Option.toObj
                        let replyToId =
                            match msg.ReplyToMessage with
                            | Some r -> Nullable r.MessageId
                            | None -> Nullable()
                        try
                            do! db.SaveChatMessage(msg.Chat.Id, msg.MessageId, userId, text, hasPhoto, hasDocument, replyToId)
                        with ex ->
                            logger.LogWarning(ex, "Failed to save community chat message {MessageId}", msg.MessageId)
        }

    let handlePrivateMessage (msg: Message) =
        task {
            let botConfig = options.Value
            use a =
                botActivity
                    .StartActivity("handlePrivateMessage")

            match msg.From with
            | Some from when msg.Chat.Type = ChatType.Private ->
                %a.SetTag("fromId", from.Id)
                %a.SetTag("text", Option.toObj msg.Text)
                let! ok = ensureCommunityMember from.Id msg.Chat.Id
                if not ok then %a.SetTag("isMember", false) else

                %a.SetTag("isMember", true)
                let! user =
                    { id = from.Id
                      username = Option.toObj from.Username
                      first_name = from.FirstName
                      last_name = Option.toObj from.LastName
                      created_at = time.GetUtcNow().UtcDateTime
                      updated_at = time.GetUtcNow().UtcDateTime }
                    |> db.UpsertUser

                // Pending /feedback: next non-command message is forwarded to admins.
                let isCommand =
                    msg.Text |> Option.exists (fun t -> t.StartsWith("/"))

                if isCommand then
                    // Any command cancels pending feedback (if present)
                    do! db.ClearPendingFeedback(user.id)

                    // Any command except /add cancels add wizard (if present)
                    if msg.Text <> Some "/add" && msg.Text <> Some "/a" then
                        do! db.ClearPendingAddFlow(user.id)

                    // Any command also cancels any in-flight album batch.
                    // No bulk-message edit here — the command's response is feedback enough.
                    let! abandoned = db.AbandonOpenBatchesExcept(user.id, None)
                    if abandoned.Length > 0 then
                        Metrics.batchAbandonedTotal.Add(
                            int64 abandoned.Length,
                            KeyValuePair("reason", box "command"))

                // Handle add wizard steps for non-command messages (photo / free-form inputs).
                // Important: if /feedback consumes this message, do NOT run /add implicit flow.
                let mutable handledAddFlow = false
                if not isCommand then
                    let! feedbackConsumed = db.TryConsumePendingFeedback(user.id)
                    if feedbackConsumed then
                        Metrics.feedbackTotal.Add(1L)

                        // Extract feedback content
                        let feedbackText = msg.Text |> Option.orElse msg.Caption |> Option.toObj
                        let hasMedia =
                            (msg.Photo |> Option.exists (fun p -> p.Length > 0))
                            || msg.Document.IsSome
                            || msg.Voice.IsSome
                            || msg.Video.IsSome

                        // 1. Save feedback to database
                        let! feedbackId =
                            try db.SaveUserFeedback(user.id, feedbackText, hasMedia, msg.MessageId)
                            with ex ->
                                logger.LogError(ex, "Failed to save user feedback to database")
                                task { return 0L }

                        // 2. Forward to admins (existing behavior)
                        for adminId in botConfig.FeedbackAdminIds do
                            try
                                do! tg.CallExn(Funogram.Telegram.Req.ForwardMessage.Make(adminId, msg.Chat.Id, msg.MessageId)) |> taskIgnore
                            with _ -> ()

                        // 3. Create GitHub issue (best-effort, anonymous — no username)
                        if gitHub.IsConfigured && feedbackId > 0L then
                            try
                                let! issueNumber = gitHub.CreateFeedbackIssue(feedbackText, hasMedia)
                                match issueNumber with
                                | Some num ->
                                    do! db.UpdateFeedbackGitHubIssue(feedbackId, num)
                                    try do! gitHub.AssignProductAgent(num)
                                    with ex -> logger.LogWarning(ex, "Failed to assign product agent to issue #{IssueNumber}", num)
                                | None -> ()
                            with ex ->
                                logger.LogWarning(ex, "Failed to create GitHub issue for feedback")

                        do! sendText msg.Chat.Id "Спасибо! Сообщение отправлено авторам."
                        handledAddFlow <- true
                    else
                        // Album uploads (MediaGroupId set on every photo of the album)
                        // route through the batch flow, not the single-photo wizard.
                        if msg.MediaGroupId.IsSome
                           && (msg.Photo |> Option.exists (fun p -> p.Length > 0)) then
                            let! handled = couponFlow.HandleAlbumPhoto user msg
                            handledAddFlow <- handled
                        else
                            let! handled = couponFlow.TryHandleWizardMessage user msg
                            handledAddFlow <- handled

                if handledAddFlow then
                    ()
                else

                do! commandHandler.Dispatch user msg
            | _ -> ()
        }

    member _.OnUpdate(update: Update) =
        task {
            let updateBodyJson =
                try FunogramJson.serialize update
                with e -> e.Message
            use top =
                botActivity
                    .StartActivity("onUpdate")
                    .SetTag("updateBodyObject", update)
                    .SetTag("updateBodyJson", updateBodyJson)
                    .SetTag("updateId", update.UpdateId)
            try
                logger.LogInformation("BotService.OnUpdate: UpdateId={UpdateId}, Message={HasMessage}, CallbackQuery={HasCallback}",
                    update.UpdateId, update.Message.IsSome, update.CallbackQuery.IsSome)
                match update.ChatMember, update.CallbackQuery, update.Message with
                | Some chatMemberUpdated, _, _ ->
                    membership.OnChatMemberUpdated(chatMemberUpdated)
                | None, Some cq, _ ->
                    do! callbackHandler.HandleCallbackQuery cq
                | None, None, Some msg ->
                    if (msg.Chat.Type = ChatType.Group || msg.Chat.Type = ChatType.SuperGroup)
                       && msg.Chat.Id = options.Value.CommunityChatId then
                        do! handleCommunityMessage msg
                    do! handlePrivateMessage msg
                | None, None, None ->
                    ()
            with ex ->
                if not (isNull top) then
                    %top.SetStatus(ActivityStatusCode.Error)
                    %top.SetTag("error", true)
                ExceptionDispatchInfo.Throw ex
        }
