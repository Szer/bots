namespace AlitaBot.Services

open System
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Telegram.Bot
open Telegram.Bot.Types
open AlitaBot

type BotService(
    botClient:  ITelegramBotClient,
    pipeline:   LlmPipeline,
    dossier:    DossierService,
    db:         DbService,
    botConf:    BotConfiguration,
    logger:     ILogger<BotService>,
    time:       TimeProvider
) =

    let isRelevantMessage (update: Update) =
        let msg = update.Message
        match msg with
        | null -> false
        | m ->
            let text = m.Text
            if String.IsNullOrWhiteSpace text then false
            else
                // Reply to the bot
                let isReply =
                    m.ReplyToMessage <> null
                    && m.ReplyToMessage.From <> null
                    && m.ReplyToMessage.From.Username = botConf.BotUsername

                // Mention in text (e.g. @Alita)
                let isMention =
                    text.Contains($"@{botConf.BotUsername}", StringComparison.OrdinalIgnoreCase)

                isReply || isMention

    member _.OnUpdate(update: Update) : Task = task {
        let msg = update.Message
        if isNull msg || isNull msg.From then return ()
        else

        let userId      = msg.From.Id
        let chatId      = msg.Chat.Id
        let username    = if isNull msg.From.Username then "" else msg.From.Username
        let displayName =
            let fn = msg.From.FirstName
            let ln = msg.From.LastName
            if String.IsNullOrWhiteSpace ln then fn
            else $"{fn} {ln}"
        let text = msg.Text

        if String.IsNullOrWhiteSpace text then return ()
        else

        try
            // Always log the message and upsert the person
            do! dossier.LogMessage(userId, chatId, username, displayName, text)

            // Only respond when mentioned or replied to
            if not (isRelevantMessage update) then return ()
            else

            // Load recent context for this chat
            let! recentRows = db.GetRecentMessages(chatId, botConf.ConversationContextMessages)
            let context =
                recentRows
                |> Array.map (fun r -> r.Message)
                |> Array.toList

            // Run the 3-layer pipeline
            let! replyOpt = pipeline.Generate(userId, username, text, context)

            match replyOpt with
            | None     -> ()   // Layer 3 chose silence
            | Some reply ->
                let replyParams = Telegram.Bot.Requests.SendMessageRequest(ChatId = ChatId(chatId), Text = reply)
                replyParams.ReplyParameters <- ReplyParameters(MessageId = msg.MessageId)
                do! botClient.SendRequest(replyParams) :> Task
        with ex ->
            logger.LogError(ex, "BotService: unhandled error for update {UpdateId}", update.Id)
    }
