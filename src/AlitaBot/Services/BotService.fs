namespace AlitaBot.Services

open System
open System.Collections.Generic
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Funogram.Telegram.Types
open AlitaBot
open AlitaBot.Llm
open AlitaBot.Telemetry
open BotInfra

module Req = Funogram.Telegram.Req

type BotService(
    options: IOptions<BotConfiguration>,
    db: DbService,
    responder: ResponderService,
    tg: ITelegramApi,
    speech: ISpeech,
    chat: IChatCompletion,
    imageGen: IImageGen,
    logger: ILogger<BotService>,
    time: TimeProvider
) =
    let nameTriggerRegex = Regex(@"(?i)\bалита\b|\balita\b", RegexOptions.Compiled)

    /// Transcripts longer than this get a one-line TL;DR appended (outside the blockquote).
    [<Literal>]
    let TldrThreshold = 400

    /// Bot API 7.7+ entity type — collapses long quoted text behind a "Show more" toggle.
    /// Funogram's MessageEntity.Type is a plain string (no closed DU), so any Bot-API-valid
    /// value passes straight through the wire; no Funogram version bump needed to use it.
    [<Literal>]
    let ExpandableBlockquote = "expandable_blockquote"

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

    /// Checks a `@username` mention against whichever entity array corresponds to
    /// `text` — `Entities` for a text message, `CaptionEntities` for a photo's caption
    /// (Telegram never populates both on the same message, so trying both is safe).
    let mentionsBot (conf: BotConfiguration) (text: string) (msg: Message) =
        let mention = "@" + conf.BotUsername
        let matchesIn (entities: MessageEntity[]) =
            entities
            |> Array.exists (fun e ->
                e.Type = "mention"
                && int e.Offset + int e.Length <= text.Length
                && text.Substring(int e.Offset, int e.Length) = mention)
        match msg.Entities with
        | Some entities when matchesIn entities -> true
        | _ ->
            match msg.CaptionEntities with
            | Some entities -> matchesIn entities
            | None -> false

    let isReplyToBot (conf: BotConfiguration) (msg: Message) =
        match msg.ReplyToMessage with
        | Some reply -> reply.From |> Option.exists (fun u -> u.Username = Some conf.BotUsername)
        | None -> false

    let isTriggered (conf: BotConfiguration) (text: string) (msg: Message) =
        mentionsBot conf text msg || isReplyToBot conf msg || nameTriggerRegex.IsMatch text

    /// (fileId, duration) of a Voice/VideoNote/Audio message, or None for anything else.
    let voiceSource (msg: Message) : string option =
        match msg.Voice with
        | Some v -> Some v.FileId
        | None ->
            match msg.VideoNote with
            | Some vn -> Some vn.FileId
            | None ->
                match msg.Audio with
                | Some a -> Some a.FileId
                | None -> None

    /// Truncation length for the /img photo caption's echoed prompt (plan §B2).
    [<Literal>]
    let ImgCaptionPromptMaxLen = 100

    /// Recognizes `/img`, `!img`, and `/img@{botUsername}` command prefixes (case-sensitive,
    /// matching Telegram convention) at the start of a message. Returns the rest of the text,
    /// trimmed, as the prompt — an empty string means "no prompt supplied" (caller replies with
    /// a usage hint); `None` means the text isn't one of these commands at all. Deliberately
    /// minimal/local parsing for this slice — Slice 4 grows this into a proper command
    /// dispatcher shared across command types.
    let tryParseCommand (conf: BotConfiguration) (text: string) : string option =
        [ "/img@" + conf.BotUsername; "/img"; "!img" ]
        |> List.tryFind (fun prefix ->
            text.StartsWith(prefix, StringComparison.Ordinal)
            && (text.Length = prefix.Length || Char.IsWhiteSpace text[prefix.Length]))
        |> Option.map (fun prefix -> text.Substring(prefix.Length).Trim())

    let logRow (chatId: int64) (messageId: int64) (userId: int64) (username: string) (displayName: string) (isBot: bool) (replyTo: int64 option) (text: string) : MessageLogRow =
        { chat_id = chatId
          message_id = messageId
          user_id = userId
          username = username
          display_name = displayName
          is_bot = isBot
          reply_to_message_id = (match replyTo with Some r -> Nullable r | None -> Nullable())
          text = text
          sent_at = time.GetUtcNow().UtcDateTime }

    let tldrRequest (conf: BotConfiguration) (transcript: string) : ChatRequest =
        { Deployment = conf.LlmDeployment
          Messages =
            [ { Role = ChatRole.System
                Content =
                  [ ContentPart.Text
                        "Summarize the following voice-message transcript in ONE short sentence, in the same language as the transcript. Output only the summary, no preamble, no quotes." ]
                ToolCalls = []
                ToolCallId = None }
              { Role = ChatRole.User
                Content = [ ContentPart.Text transcript ]
                ToolCalls = []
                ToolCallId = None } ]
          Tools = []
          Temperature = None
          MaxTokens = Some 60 }

    /// Cheap non-stream TL;DR for long transcripts. Best-effort: any failure just
    /// means the reply ships without a TL;DR line, never blocks the transcript reply.
    let tryTldr (conf: BotConfiguration) (transcript: string) =
        task {
            if transcript.Length <= TldrThreshold then
                return None
            else
                match! chat.Complete(tldrRequest conf transcript, CancellationToken.None) with
                | Ok resp when not (String.IsNullOrWhiteSpace resp.Text) -> return Some(resp.Text.Trim())
                | Ok _ -> return None
                | Error err ->
                    logger.LogWarning("TL;DR generation failed for a voice transcript: {Error}", string err)
                    return None
        }

    /// Shared by plain text messages and photo messages: logs `logText` to message_log,
    /// checks `triggerText` (raw text/caption, matching whatever entity array the mention
    /// offsets are relative to) for a trigger, and dispatches to the responder.
    let handleTriggerableMessage (conf: BotConfiguration) (msg: Message) (from: User) (logText: string) (triggerText: string) =
        task {
            use a = botActivity.StartActivity("handleMessage")
            %a.SetTag("chatId", msg.Chat.Id)
            %a.SetTag("fromId", from.Id)

            do! db.LogMessage(
                    logRow msg.Chat.Id msg.MessageId from.Id (Option.toObj from.Username) (displayNameOf from) from.IsBot
                        (match msg.ReplyToMessage with Some r -> Some r.MessageId | None -> None) logText)

            if isTriggered conf triggerText msg then
                match! responder.Respond(msg) with
                | Some(sent, replyText) ->
                    do! db.LogMessage(
                            logRow msg.Chat.Id sent.MessageId (botUserId conf) conf.BotUsername conf.BotUsername true
                                (Some msg.MessageId) replyText)
                    %a.SetTag("outcome", "replied")
                    countOutcome "replied"
                | None ->
                    %a.SetTag("outcome", "logged")
                    countOutcome "logged"
            else
                %a.SetTag("outcome", "logged")
                countOutcome "logged"
        }

    let handleMessage (conf: BotConfiguration) (msg: Message) (from: User) (text: string) =
        handleTriggerableMessage conf msg from text text

    /// Photo message flow: logs as `[photo] {caption}` (bare `[photo]` for no caption,
    /// mirroring S1's `[voice]` convention) and checks the RAW caption — not the logged
    /// text — against CaptionEntities for a mention, since entity offsets are relative to
    /// the caption Telegram sent, not our logging prefix. The image itself is fetched by
    /// ResponderService at respond time (from msg.Photo directly, via ITelegramApi), not
    /// stored here — message_log only ever holds text.
    let handlePhotoMessage (conf: BotConfiguration) (msg: Message) (from: User) =
        let caption = msg.Caption |> Option.defaultValue ""
        let logText = if caption = "" then "[photo]" else $"[photo] {caption}"
        handleTriggerableMessage conf msg from logText caption

    /// Voice/VideoNote/Audio flow: download -> transcribe -> reply as an expandable
    /// blockquote (+ TL;DR when long) -> log both the sender's transcript and the bot's
    /// reply -> optionally hand the transcript to the normal trigger/responder path
    /// (e.g. a voice message saying "алита ...") without ever auto-triggering by itself.
    let handleVoiceMessage (conf: BotConfiguration) (msg: Message) (from: User) (fileId: string) =
        task {
            use a = botActivity.StartActivity("handleVoiceMessage")
            %a.SetTag("chatId", msg.Chat.Id)
            %a.SetTag("fromId", from.Id)

            if not conf.VoiceTranscribeEnabled then
                %a.SetTag("outcome", "voice_disabled")
            else
                let! file = tg.CallExn(Req.GetFile.Make(fileId))
                match file.FilePath with
                | None ->
                    logger.LogWarning("Voice message file {FileId} has no FilePath — skipping transcription", fileId)
                    %a.SetTag("outcome", "voice_no_filepath")
                | Some filePath when String.IsNullOrWhiteSpace filePath ->
                    logger.LogWarning("Voice message file {FileId} has no FilePath — skipping transcription", fileId)
                    %a.SetTag("outcome", "voice_no_filepath")
                | Some filePath ->
                    let! bytes = tg.DownloadFile filePath
                    match! speech.Transcribe(bytes, CancellationToken.None) with
                    | Error err ->
                        logger.LogWarning("Voice transcription failed: {Error}", string err)
                        %a.SetTag("outcome", "voice_transcribe_failed")
                    | Ok transcript when String.IsNullOrWhiteSpace transcript ->
                        %a.SetTag("outcome", "voice_empty_transcript")
                    | Ok transcript ->
                        // Sender's voice content enters the conversational log verbatim
                        // (prefixed) so it feeds later LLM context like any text message.
                        do! db.LogMessage(
                                logRow msg.Chat.Id msg.MessageId from.Id (Option.toObj from.Username) (displayNameOf from) from.IsBot
                                    (match msg.ReplyToMessage with Some r -> Some r.MessageId | None -> None)
                                    $"[voice] {transcript}")

                        let! tldr = tryTldr conf transcript
                        let quoted = $"🎙️ {transcript}"
                        let entities = [| MessageEntity.Create(``type`` = ExpandableBlockquote, offset = 0L, length = int64 quoted.Length) |]
                        let fullText =
                            match tldr with
                            | Some t -> $"{quoted}\n\nTL;DR: {t}"
                            | None -> quoted

                        let! sent = BotHelpers.sendTextReplyWithEntities tg msg.Chat.Id fullText entities msg.MessageId
                        do! db.LogMessage(
                                logRow msg.Chat.Id sent.MessageId (botUserId conf) conf.BotUsername conf.BotUsername true
                                    (Some msg.MessageId) fullText)

                        // Transcription itself never triggers the responder — but a
                        // transcript that independently matches the normal trigger rules
                        // (name/mention/reply-to-bot) may, exactly like a text message.
                        let transcribedMsg = { msg with Text = Some transcript }
                        if isTriggered conf transcript transcribedMsg then
                            match! responder.Respond(transcribedMsg) with
                            | Some(replySent, replyText) ->
                                do! db.LogMessage(
                                        logRow msg.Chat.Id replySent.MessageId (botUserId conf) conf.BotUsername conf.BotUsername true
                                            (Some msg.MessageId) replyText)
                                %a.SetTag("outcome", "voice_transcribed_and_triggered")
                                countOutcome "voice_transcribed_and_triggered"
                            | None ->
                                %a.SetTag("outcome", "voice_transcribed")
                                countOutcome "voice_transcribed"
                        else
                            %a.SetTag("outcome", "voice_transcribed")
                            countOutcome "voice_transcribed"
        }

    /// Downloads the largest photo of `msg`'s reply target, if any — the img2img source
    /// image for a `/img` reply-to-a-photo prompt. Best-effort: any failure (API error,
    /// missing FilePath) logs a Warning and returns None, degrading to text-to-image
    /// instead of failing the whole command (mirrors ResponderService's vision fetch).
    let tryFetchReplySourceImage (msg: Message) : Task<byte[] option> =
        task {
            match msg.ReplyToMessage |> Option.bind BotHelpers.largestPhoto with
            | None -> return None
            | Some photo ->
                try
                    let! file = tg.CallExn(Req.GetFile.Make(photo.FileId))
                    match file.FilePath with
                    | None -> return None
                    | Some fp when String.IsNullOrWhiteSpace fp -> return None
                    | Some fp ->
                        let! bytes = tg.DownloadFile fp
                        return Some bytes
                with ex ->
                    logger.LogWarning(
                        ex,
                        "Image gen: failed to fetch reply-to photo {FileId} — falling back to text-to-image",
                        photo.FileId)
                    return None
        }

    /// `/img` / `!img` command flow (plan §B2). Always logs the command as
    /// `[img-cmd] {prompt}` first, then branches: empty prompt -> RU usage hint;
    /// IMAGE_GEN_ENABLED=false -> RU "disabled" reply; otherwise sends a "рисую..."
    /// placeholder, resolves an optional img2img source image from the reply target,
    /// generates, deletes the placeholder and sends the photo (or edits the placeholder
    /// into an apology on failure). Never dispatches to ResponderService — a command
    /// message is fully handled here, regardless of whether it also happens to contain
    /// the bot's name/mention.
    let handleImageCommand (conf: BotConfiguration) (msg: Message) (from: User) (prompt: string) =
        task {
            use a = botActivity.StartActivity("handleImageCommand")
            %a.SetTag("chatId", msg.Chat.Id)
            %a.SetTag("fromId", from.Id)

            do! db.LogMessage(
                    logRow msg.Chat.Id msg.MessageId from.Id (Option.toObj from.Username) (displayNameOf from) from.IsBot
                        (match msg.ReplyToMessage with Some r -> Some r.MessageId | None -> None)
                        $"[img-cmd] {prompt}")

            if String.IsNullOrWhiteSpace prompt then
                let hint =
                    "Напиши, что нарисовать: `/img рыжий кот на подоконнике`. Ответь этой командой на фото — перерисую его по описанию."
                let! sent = BotHelpers.sendTextReply tg msg.Chat.Id hint msg.MessageId
                do! db.LogMessage(
                        logRow msg.Chat.Id sent.MessageId (botUserId conf) conf.BotUsername conf.BotUsername true
                            (Some msg.MessageId) hint)
                %a.SetTag("outcome", "image_empty_prompt")
                countOutcome "image_empty_prompt"
            elif not conf.ImageGenEnabled then
                let disabledText = "Генерация картинок сейчас выключена."
                let! sent = BotHelpers.sendTextReply tg msg.Chat.Id disabledText msg.MessageId
                do! db.LogMessage(
                        logRow msg.Chat.Id sent.MessageId (botUserId conf) conf.BotUsername conf.BotUsername true
                            (Some msg.MessageId) disabledText)
                %a.SetTag("outcome", "image_disabled")
                countOutcome "image_disabled"
            else
                let! sourceImage = tryFetchReplySourceImage msg
                let! placeholder = BotHelpers.sendTextReply tg msg.Chat.Id "рисую..." msg.MessageId

                match! imageGen.Generate(prompt, sourceImage, CancellationToken.None) with
                | Ok(bytes, _usage) ->
                    let truncated =
                        if prompt.Length > ImgCaptionPromptMaxLen then prompt.Substring(0, ImgCaptionPromptMaxLen)
                        else prompt
                    let caption =
                        match ImagePricing.tryCost logger conf.LlmPricingJson conf.ImageDeployment conf.ImageQuality with
                        | Some cost ->
                            let costStr = cost.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                            $"{truncated}\n${costStr}"
                        | None -> truncated
                    let! sentPhoto = BotHelpers.sendPhotoReply tg msg.Chat.Id bytes caption msg.MessageId
                    do! db.LogMessage(
                            logRow msg.Chat.Id sentPhoto.MessageId (botUserId conf) conf.BotUsername conf.BotUsername true
                                (Some msg.MessageId) $"[image] {truncated}")
                    do! BotHelpers.deleteMessage tg msg.Chat.Id placeholder.MessageId
                    let outcome = if sourceImage.IsSome then "image_edited" else "image_generated"
                    %a.SetTag("outcome", outcome)
                    countOutcome outcome
                | Error err ->
                    logger.LogWarning("Image generation failed: {Error}", string err)
                    do! BotHelpers.editMessageText tg msg.Chat.Id placeholder.MessageId "Не получилось нарисовать 🙁"
                    %a.SetTag("outcome", "image_failed")
                    countOutcome "image_failed"
        }

    member _.OnUpdate(update: Update) =
        task {
            let conf = options.Value
            match update.Message with
            | Some msg ->
                match msg.From with
                | Some from ->
                    match msg.Text with
                    | Some text ->
                        if conf.TargetChatIds |> List.contains msg.Chat.Id then
                            match tryParseCommand conf text with
                            | Some prompt -> do! handleImageCommand conf msg from prompt
                            | None -> do! handleMessage conf msg from text
                        else
                            countOutcome "ignored"
                    | None ->
                        match BotHelpers.largestPhoto msg with
                        | Some _ ->
                            if conf.TargetChatIds |> List.contains msg.Chat.Id then
                                do! handlePhotoMessage conf msg from
                            // else: silently ignored — same privacy gate as text messages.
                        | None ->
                            match voiceSource msg with
                            | Some fileId ->
                                if conf.TargetChatIds |> List.contains msg.Chat.Id then
                                    do! handleVoiceMessage conf msg from fileId
                                // else: silently ignored — same privacy gate as text messages.
                            | None -> ()
                | None -> ()
            | None -> ()
        }
