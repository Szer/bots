namespace AlitaBot.Services

open System
open System.Collections.Generic
open System.Text.RegularExpressions
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Funogram.Telegram.Types
open AlitaBot
open AlitaBot.Telemetry
open BotInfra

type BotService(
    options: IOptions<BotConfiguration>,
    db: DbService,
    responder: ResponderService,
    logger: ILogger<BotService>,
    time: TimeProvider
) =
    let nameTriggerRegex = Regex(@"(?i)\bалита\b|\balita\b", RegexOptions.Compiled)

    let countOutcome (outcome: string) =
        Metrics.messagesTotal.Add(1L, KeyValuePair("outcome", box outcome))

    let displayNameOf (u: User) =
        match u.LastName with
        | Some last -> $"{u.FirstName} {last}"
        | None -> u.FirstName

    /// Bot user id is the numeric prefix of the bot token ("123456:ABC-..." -> 123456).
    let botUserId (conf: BotConfiguration) =
        match conf.BotToken.Split(':') with
        | [||] -> 0L
        | parts ->
            match Int64.TryParse parts[0] with
            | true, v -> v
            | _ -> 0L

    let mentionsBot (conf: BotConfiguration) (text: string) (msg: Message) =
        let mention = "@" + conf.BotUsername
        match msg.Entities with
        | Some entities ->
            entities
            |> Array.exists (fun e ->
                e.Type = "mention"
                && int e.Offset + int e.Length <= text.Length
                && text.Substring(int e.Offset, int e.Length) = mention)
        | None -> false

    let isReplyToBot (conf: BotConfiguration) (msg: Message) =
        match msg.ReplyToMessage with
        | Some reply -> reply.From |> Option.exists (fun u -> u.Username = Some conf.BotUsername)
        | None -> false

    let isTriggered (conf: BotConfiguration) (text: string) (msg: Message) =
        mentionsBot conf text msg || isReplyToBot conf msg || nameTriggerRegex.IsMatch text

    let handleMessage (conf: BotConfiguration) (msg: Message) (from: User) (text: string) =
        task {
            use a = botActivity.StartActivity("handleMessage")
            %a.SetTag("chatId", msg.Chat.Id)
            %a.SetTag("fromId", from.Id)

            do! db.LogMessage
                    { chat_id = msg.Chat.Id
                      message_id = msg.MessageId
                      user_id = from.Id
                      username = Option.toObj from.Username
                      display_name = displayNameOf from
                      is_bot = from.IsBot
                      reply_to_message_id =
                        match msg.ReplyToMessage with
                        | Some r -> Nullable r.MessageId
                        | None -> Nullable()
                      text = text
                      sent_at = time.GetUtcNow().UtcDateTime }

            if isTriggered conf text msg then
                match! responder.Respond(msg) with
                | Some(sent, replyText) ->
                    do! db.LogMessage
                            { chat_id = msg.Chat.Id
                              message_id = sent.MessageId
                              user_id = botUserId conf
                              username = conf.BotUsername
                              display_name = conf.BotUsername
                              is_bot = true
                              reply_to_message_id = Nullable msg.MessageId
                              text = replyText
                              sent_at = time.GetUtcNow().UtcDateTime }
                    %a.SetTag("outcome", "replied")
                    countOutcome "replied"
                | None ->
                    %a.SetTag("outcome", "logged")
                    countOutcome "logged"
            else
                %a.SetTag("outcome", "logged")
                countOutcome "logged"
        }

    member _.OnUpdate(update: Update) =
        task {
            let conf = options.Value
            match update.Message with
            | Some msg ->
                match msg.Text, msg.From with
                | Some text, Some from ->
                    if conf.TargetChatIds |> List.contains msg.Chat.Id then
                        do! handleMessage conf msg from text
                    else
                        countOutcome "ignored"
                | _ -> ()
            | None -> ()
        }
