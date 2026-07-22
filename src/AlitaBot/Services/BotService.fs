namespace AlitaBot.Services

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Text.Json
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

// ── Social engine (Slice 7) request/response shapes ─────────────────────────
//
// Module-level (not nested in BotService) — F# doesn't support type declarations inside
// a class's `let`-bound implementation section, only at module/namespace scope.

/// `/roast`'s resolved target: a user_id plus the display name shown in the LLM prompt.
type RoastTarget = { UserId: int64; DisplayName: string }

/// `/roast`'s gathered ammunition — see `BotService.gatherRoastAmmo`.
type RoastAmmo =
    { DossierSummary: string option
      Facts: string list
      Messages: string list }

/// One entry of the `/awards` LLM's JSON array — see `BotService.parseAwardsJson`.
type AwardEntry =
    { Title: string
      User: string
      EvidenceQuote: string }

/// The `/quote` LLM's JSON object — see `BotService.parseQuoteJson`.
type QuoteEntry =
    { Author: string
      Quote: string
      Comment: string }

type BotService(
    options: IOptions<BotConfiguration>,
    db: DbService,
    responder: ResponderService,
    tg: ITelegramApi,
    speech: ISpeech,
    chat: IChatCompletion,
    imageGen: IImageGen,
    embeddings: IEmbeddings,
    settingsReloader: ISettingsReloader,
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

    /// Serializes all processing for one chat (SemaphoreSlim, count 1): a burst of
    /// rapid triggers in the same chat — or a webhook redelivery racing the original
    /// attempt — runs one at a time instead of two concurrent LLM streams reading the
    /// same message_log snapshot and posting overlapping replies. Bounded by
    /// TargetChatIds (the bot only ever locks chats it's configured to listen to), so
    /// the dictionary can't grow unbounded over the process lifetime.
    let chatLocks = ConcurrentDictionary<int64, SemaphoreSlim>()

    let withChatLock (chatId: int64) (work: unit -> Task<'a>) : Task<'a> =
        task {
            let sem = chatLocks.GetOrAdd(chatId, fun _ -> new SemaphoreSlim(1, 1))
            do! sem.WaitAsync()
            try
                return! work()
            finally
                %sem.Release()
        }

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

    // ── Embedding pipeline (Slice 5a: memory foundation) ────────────────────
    //
    // Every successful message_log insert (user AND bot rows alike) gets embedded
    // in the background and written to message_embedding, feeding /ask's semantic
    // search. Entirely best-effort: an embedding failure (LLM error or exception)
    // is Warning-logged + counted (alitabot_embedding_failures_total) and never
    // affects the reply path — see tryEmbed's fireAndForget wrapping.

    /// Skips embedding pure command invocations — bare "/xxx"/"!xxx" text, or our own
    /// "[xxx-cmd] ..." message_log tagging convention (handleSimpleCommand /
    /// handleImageCommand / handleSummaryCommand / handleAskCommand) — neither carries
    /// conversational content worth indexing for semantic search.
    let isPureCommandText (text: string) =
        let t = text.TrimStart()
        t.StartsWith("/") || t.StartsWith("!") || (t.StartsWith("[") && t.Contains("-cmd]"))

    /// Embeds `text` (batch of 1) and inserts the resulting message_embedding row for
    /// `messageLogId`, fully in the background (BotInfra.Utils.fireAndForget — catches
    /// and Warning-logs any exception on top of the explicit LlmError handling below).
    /// No-ops entirely when EMBED_MESSAGES=false, `text` is shorter than
    /// EMBEDDING_MIN_CHARS, or it looks like a pure command (isPureCommandText).
    let tryEmbed (conf: BotConfiguration) (chatId: int64) (userId: int64) (messageLogId: int64) (text: string) =
        if conf.EmbedMessagesEnabled && text.Length >= conf.EmbeddingMinChars && not (isPureCommandText text) then
            fireAndForget logger "embedding.pipeline" (fun () ->
                task {
                    // Slice 5b: an opted-out author's (message_log.user_id, not the bot's
                    // own reply — bot replies are never opted out) messages are never
                    // embedded, mirroring their exclusion from the nightly dossier job and
                    // from recall injection. Checked here, not at the message_log write
                    // (BotService.logAndEmbed) — message_log itself is the shared chat
                    // record, kept for everyone regardless of opt-out (see /forget-me).
                    let! optedOut = db.IsOptedOut(userId)
                    if not optedOut then
                        let ctx: UsageContext = { ChatId = Some chatId; UserId = Some userId }
                        match! embeddings.Embed(conf.EmbeddingDeployment, [ text ], ctx, CancellationToken.None) with
                        | Ok vectors when vectors.Length > 0 && vectors[0].Length > 0 ->
                            do! db.InsertMessageEmbedding(messageLogId, vectors[0])
                        | Ok _ -> ()
                        | Error err ->
                            Metrics.embeddingFailuresTotal.Add(1L)
                            logger.LogWarning("Embedding failed for message_log {Id}: {Error}", messageLogId, string err)
                } :> Task)

    /// Drop-in replacement for `db.LogMessage` at every call site: same `bool` contract
    /// (true = first delivery inserted, false = webhook-redelivery duplicate) every
    /// existing caller already relies on, but additionally kicks off the embedding
    /// pipeline (tryEmbed) on a real insert. A duplicate delivery is never re-embedded.
    let logAndEmbed (conf: BotConfiguration) (row: MessageLogRow) : Task<bool> =
        task {
            match! db.LogMessage(row) with
            | Some id ->
                tryEmbed conf row.chat_id row.user_id id row.text
                return true
            | None -> return false
        }

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
    let tryTldr (conf: BotConfiguration) (ctx: UsageContext) (transcript: string) =
        task {
            if transcript.Length <= TldrThreshold then
                return None
            else
                match! chat.Complete(tldrRequest conf transcript, ctx, CancellationToken.None) with
                | Ok resp when not (String.IsNullOrWhiteSpace resp.Text) -> return Some(resp.Text.Trim())
                | Ok _ -> return None
                | Error err ->
                    logger.LogWarning("TL;DR generation failed for a voice transcript: {Error}", string err)
                    return None
        }

    // ── Outcome router (Slice 6) ─────────────────────────────────────────────
    //
    // A TRIGGERED non-command message doesn't always get a text reply: OUTCOME_WEIGHTS
    // (bot_setting) rolls "reply" (normal path, unchanged) | "silence" (say nothing) |
    // "emoji" (react instead of replying) — see Services/OutcomeRouter.fs for the
    // weighted-pick itself. Defaults keep the pre-S6 behavior (always "reply").

    /// Telegram restricts message-reaction emoji to a fixed allowed set; this is the
    /// subset the emoji outcome picks from (plan §3's list) — chosen to cover a broad
    /// range of reactions without listing Telegram's entire allowed-emoji table.
    let allowedReactionEmoji =
        [| "👍"; "❤️"; "🔥"; "😁"; "🤔"; "🤯"; "😱"; "🤬"; "😢"; "🎉"; "🤩"; "💩"; "🤡"; "🥱" |]

    let emojiPickRequest (conf: BotConfiguration) (text: string) : ChatRequest =
        let allowedText = String.concat "" allowedReactionEmoji
        { Deployment = conf.LlmDeployment
          Messages =
            [ { Role = ChatRole.System
                Content =
                  [ ContentPart.Text
                        $"Choose ONE fitting emoji reaction from this allowed set: {allowedText}. Output ONLY that one emoji, nothing else — no words, no punctuation." ]
                ToolCalls = []
                ToolCallId = None }
              { Role = ChatRole.User
                Content = [ ContentPart.Text text ]
                ToolCalls = []
                ToolCallId = None } ]
          Tools = []
          Temperature = None
          MaxTokens = Some 10 }

    /// "emoji" outcome: a tiny non-stream LLM call picks one emoji from
    /// `allowedReactionEmoji`, then `Req.SetMessageReaction` reacts to the triggering
    /// message — no text reply. Best-effort: a malformed/unlisted model answer falls back
    /// to the set's first emoji rather than refusing to react at all; a failed LLM call or
    /// a rejected SetMessageReaction call is Warning-logged and simply skipped (the
    /// message stays logged either way — this never blocks or retries the trigger).
    let handleEmojiOutcome (conf: BotConfiguration) (msg: Message) (from: User) =
        task {
            let text = msg.Text |> Option.orElse msg.Caption |> Option.defaultValue ""
            let ctx: UsageContext = { ChatId = Some msg.Chat.Id; UserId = Some from.Id }
            match! chat.Complete(emojiPickRequest conf text, ctx, CancellationToken.None) with
            | Error err -> logger.LogWarning("Outcome=emoji: emoji-pick LLM call failed: {Error}", string err)
            | Ok resp ->
                let picked = resp.Text.Trim()
                let emoji = if Array.contains picked allowedReactionEmoji then picked else allowedReactionEmoji[0]
                let reaction = [| ReactionType.Emoji(ReactionTypeEmoji.Create(``type`` = "emoji", emoji = emoji)) |]
                try
                    do! tg.CallExn(Req.SetMessageReaction.Make(msg.Chat.Id, msg.MessageId, reaction = reaction)) |> taskIgnore
                with ex ->
                    logger.LogWarning(ex, "Outcome=emoji: SetMessageReaction failed for message {MessageId}", msg.MessageId)
        }

    /// Shared by plain text messages and photo messages: logs `logText` to message_log,
    /// checks `triggerText` (raw text/caption, matching whatever entity array the mention
    /// offsets are relative to) for a trigger, and — when triggered — rolls the outcome
    /// router before dispatching to the responder.
    let handleTriggerableMessage (conf: BotConfiguration) (msg: Message) (from: User) (logText: string) (triggerText: string) =
        task {
            use a = botActivity.StartActivity("handleMessage")
            %a.SetTag("chatId", msg.Chat.Id)
            %a.SetTag("fromId", from.Id)

            let! inserted =
                logAndEmbed conf (
                    logRow msg.Chat.Id msg.MessageId from.Id (Option.toObj from.Username) (displayNameOf from) from.IsBot
                        (match msg.ReplyToMessage with Some r -> Some r.MessageId | None -> None) logText)

            if not inserted then
                // A webhook redelivery of an update we already fully handled (e.g. Telegram
                // retried after a slow LLM reply held the connection open past its timeout).
                // message_log's UNIQUE(chat_id, message_id) is the idempotency source of
                // truth: skip re-triggering the responder so the retry can't send a second,
                // distinct reply on top of the one already sent.
                %a.SetTag("outcome", "duplicate_update")
                countOutcome "duplicate_update"
            elif isTriggered conf triggerText msg then
                let weights = OutcomeRouter.parseWeights conf.OutcomeWeightsJson
                match OutcomeRouter.pick weights (Random.Shared.NextDouble()) with
                | OutcomeRouter.Silence ->
                    %a.SetTag("outcome", "silence")
                    countOutcome "silence"
                | OutcomeRouter.Emoji ->
                    do! handleEmojiOutcome conf msg from
                    %a.SetTag("outcome", "emoji")
                    countOutcome "emoji"
                | _ ->
                    match! responder.Respond(msg) with
                    | Some(sent, replyText) ->
                        do! logAndEmbed conf (
                                logRow msg.Chat.Id sent.MessageId (botUserId conf) conf.BotUsername conf.BotUsername true
                                    (Some msg.MessageId) replyText)
                            |> taskIgnore
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

            let countVoice (outcome: string) =
                Metrics.voiceTranscribeTotal.Add(1L, KeyValuePair("outcome", box outcome))

            let usageCtx: UsageContext = { ChatId = Some msg.Chat.Id; UserId = Some from.Id }

            if not conf.VoiceTranscribeEnabled then
                %a.SetTag("outcome", "voice_disabled")
                countVoice "disabled"
            else
                // Covers Telegram file download + the STT call — not recorded for the
                // voice_disabled case above, since no transcription is attempted there.
                let sw = Stopwatch.StartNew()
                let! file = tg.CallExn(Req.GetFile.Make(fileId))
                match file.FilePath with
                | None ->
                    logger.LogWarning("Voice message file {FileId} has no FilePath — skipping transcription", fileId)
                    %a.SetTag("outcome", "voice_no_filepath")
                    countVoice "no_filepath"
                | Some filePath when String.IsNullOrWhiteSpace filePath ->
                    logger.LogWarning("Voice message file {FileId} has no FilePath — skipping transcription", fileId)
                    %a.SetTag("outcome", "voice_no_filepath")
                    countVoice "no_filepath"
                | Some filePath ->
                    let! bytes = tg.DownloadFile filePath
                    match! speech.Transcribe(bytes, usageCtx, CancellationToken.None) with
                    | Error err ->
                        Metrics.voiceTranscribeDurationMs.Record(sw.Elapsed.TotalMilliseconds)
                        logger.LogWarning("Voice transcription failed: {Error}", string err)
                        %a.SetTag("outcome", "voice_transcribe_failed")
                        countVoice "failed"
                    | Ok transcript when String.IsNullOrWhiteSpace transcript ->
                        Metrics.voiceTranscribeDurationMs.Record(sw.Elapsed.TotalMilliseconds)
                        %a.SetTag("outcome", "voice_empty_transcript")
                        countVoice "empty_transcript"
                    | Ok transcript ->
                        Metrics.voiceTranscribeDurationMs.Record(sw.Elapsed.TotalMilliseconds)
                        countVoice "transcribed"

                        // Sender's voice content enters the conversational log verbatim
                        // (prefixed) so it feeds later LLM context like any text message.
                        let! inserted =
                            logAndEmbed conf (
                                logRow msg.Chat.Id msg.MessageId from.Id (Option.toObj from.Username) (displayNameOf from) from.IsBot
                                    (match msg.ReplyToMessage with Some r -> Some r.MessageId | None -> None)
                                    $"[voice] {transcript}")

                        if not inserted then
                            // Webhook redelivery: the STT call above is unavoidably repeated
                            // (the log text is only known after transcribing), but
                            // message_log's UNIQUE constraint still stops a second reply
                            // from going out for the same voice message.
                            %a.SetTag("outcome", "voice_duplicate_update")
                            countOutcome "voice_duplicate_update"
                        else

                        let! tldr = tryTldr conf usageCtx transcript
                        let quoted = $"🎙️ {transcript}"
                        let entities = [| MessageEntity.Create(``type`` = ExpandableBlockquote, offset = 0L, length = int64 quoted.Length) |]
                        let fullText =
                            match tldr with
                            | Some t -> $"{quoted}\n\nTL;DR: {t}"
                            | None -> quoted

                        let! sent = BotHelpers.sendTextReplyWithEntities tg msg.Chat.Id fullText entities msg.MessageId
                        do! logAndEmbed conf (
                                logRow msg.Chat.Id sent.MessageId (botUserId conf) conf.BotUsername conf.BotUsername true
                                    (Some msg.MessageId) fullText)
                            |> taskIgnore

                        // Transcription itself never triggers the responder — but a
                        // transcript that independently matches the normal trigger rules
                        // (name/mention/reply-to-bot) may, exactly like a text message.
                        let transcribedMsg = { msg with Text = Some transcript }
                        if isTriggered conf transcript transcribedMsg then
                            match! responder.Respond(transcribedMsg) with
                            | Some(replySent, replyText) ->
                                do! logAndEmbed conf (
                                        logRow msg.Chat.Id replySent.MessageId (botUserId conf) conf.BotUsername conf.BotUsername true
                                            (Some msg.MessageId) replyText)
                                    |> taskIgnore
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
    /// the bot's name/mention. `alitabot_command_total` is incremented centrally by the
    /// dispatcher (see `commands`/OnUpdate), not here.
    let handleImageCommand (conf: BotConfiguration) (msg: Message) (from: User) (prompt: string) =
        task {
            use a = botActivity.StartActivity("handleImageCommand")
            %a.SetTag("chatId", msg.Chat.Id)
            %a.SetTag("fromId", from.Id)

            let! inserted =
                logAndEmbed conf (
                    logRow msg.Chat.Id msg.MessageId from.Id (Option.toObj from.Username) (displayNameOf from) from.IsBot
                        (match msg.ReplyToMessage with Some r -> Some r.MessageId | None -> None)
                        $"[img-cmd] {prompt}")

            if not inserted then
                // Webhook redelivery of a command we already handled (image generation is
                // the slowest path in the bot — the one most likely to outlast Telegram's
                // webhook timeout and trigger a retry). Skip re-generating and re-sending.
                %a.SetTag("outcome", "duplicate_update")
                countOutcome "duplicate_update"
            elif String.IsNullOrWhiteSpace prompt then
                let hint =
                    "Напиши, что нарисовать: `/img рыжий кот на подоконнике`. Ответь этой командой на фото — перерисую его по описанию."
                let! sent = BotHelpers.sendTextReply tg msg.Chat.Id hint msg.MessageId
                do! logAndEmbed conf (
                        logRow msg.Chat.Id sent.MessageId (botUserId conf) conf.BotUsername conf.BotUsername true
                            (Some msg.MessageId) hint)
                    |> taskIgnore
                %a.SetTag("outcome", "image_empty_prompt")
                countOutcome "image_empty_prompt"
            elif not conf.ImageGenEnabled then
                let disabledText = "Генерация картинок сейчас выключена."
                let! sent = BotHelpers.sendTextReply tg msg.Chat.Id disabledText msg.MessageId
                do! logAndEmbed conf (
                        logRow msg.Chat.Id sent.MessageId (botUserId conf) conf.BotUsername conf.BotUsername true
                            (Some msg.MessageId) disabledText)
                    |> taskIgnore
                %a.SetTag("outcome", "image_disabled")
                countOutcome "image_disabled"
            else
                let! sourceImage = tryFetchReplySourceImage msg
                let! placeholder = BotHelpers.sendTextReply tg msg.Chat.Id "рисую..." msg.MessageId
                let usageCtx: UsageContext = { ChatId = Some msg.Chat.Id; UserId = Some from.Id }

                match! imageGen.Generate(prompt, sourceImage, usageCtx, CancellationToken.None) with
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
                    do! logAndEmbed conf (
                            logRow msg.Chat.Id sentPhoto.MessageId (botUserId conf) conf.BotUsername conf.BotUsername true
                                (Some msg.MessageId) $"[image] {truncated}")
                        |> taskIgnore
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

    // ── Command registry (Phase-1 Slice 4) ──────────────────────────────────
    //
    // Grows S3's single-purpose /img parsing into a small registry: name, aliases,
    // description, handler (Commands.fs). /help is auto-generated from it, so there's
    // no separate hand-written command list to fall out of sync. `alitabot_command_total`
    // is incremented once per dispatch, centrally, in OnUpdate — individual handlers don't
    // touch it (see handleImageCommand's comment).

    /// Shared skeleton for a "simple" command (/help, /usage, /model): logs the incoming
    /// command message as `[<name>-cmd] {args}` — idempotent, same webhook-redelivery
    /// guard as handleImageCommand/handleTriggerableMessage — then on first delivery runs
    /// `body ()` to produce (replyText, outcome), sends it as a normal reply, and logs the
    /// bot's own reply row. /summary doesn't use this: its reply is (probably) ephemeral,
    /// not a plain sendTextReply, and it has more outcome branches.
    let handleSimpleCommand
        (name: string)
        (conf: BotConfiguration)
        (msg: Message)
        (from: User)
        (args: string)
        (body: unit -> Task<string * string>)
        =
        task {
            use a = botActivity.StartActivity($"handle{name}Command")
            %a.SetTag("chatId", msg.Chat.Id)
            %a.SetTag("fromId", from.Id)

            let! inserted =
                logAndEmbed conf (
                    logRow msg.Chat.Id msg.MessageId from.Id (Option.toObj from.Username) (displayNameOf from) from.IsBot
                        (match msg.ReplyToMessage with Some r -> Some r.MessageId | None -> None)
                        (if args = "" then $"[{name}-cmd]" else $"[{name}-cmd] {args}"))

            if not inserted then
                %a.SetTag("outcome", "duplicate_update")
                countOutcome "duplicate_update"
            else
                let! replyText, outcome = body()
                let! sent = BotHelpers.sendTextReply tg msg.Chat.Id replyText msg.MessageId
                do! logAndEmbed conf (
                        logRow msg.Chat.Id sent.MessageId (botUserId conf) conf.BotUsername conf.BotUsername true
                            (Some msg.MessageId) replyText)
                    |> taskIgnore
                %a.SetTag("outcome", outcome)
                countOutcome outcome
        }

    /// Lenient parse of the MODEL_ALLOWLIST bot_setting (JSON_BLOB array of strings),
    /// e.g. ["alita-gpt-5-mini"]. Malformed JSON or a non-array value -> [] (nothing
    /// switchable — /model's arg form always refuses until the setting is fixed).
    let parseModelAllowlist (json: string) : string list =
        try
            use doc = JsonDocument.Parse(json: string)
            if doc.RootElement.ValueKind <> JsonValueKind.Array then
                []
            else
                [ for el in doc.RootElement.EnumerateArray() do
                    if el.ValueKind = JsonValueKind.String then el.GetString() ]
        with _ -> []

    /// `/model` — no arg: shows the current LLM_DEPLOYMENT + the MODEL_ALLOWLIST.
    /// With an arg: if it's in MODEL_ALLOWLIST, upserts LLM_DEPLOYMENT and reloads the
    /// live BotConfiguration in-process (ISettingsReloader — the same path
    /// `/reload-settings` uses) so the switch takes effect on the very next LLM call,
    /// not just after a restart; otherwise a RU refusal + the allowlist.
    let handleModelCommand (conf: BotConfiguration) (msg: Message) (from: User) (args: string) =
        handleSimpleCommand "model" conf msg from args (fun () ->
            task {
                let allowlist = parseModelAllowlist conf.ModelAllowlistJson
                let listText = if allowlist.IsEmpty then "(пусто)" else allowlist |> String.concat ", "

                if String.IsNullOrWhiteSpace args then
                    return $"Текущая модель: {conf.LlmDeployment}\nДоступные модели: {listText}", "model_shown"
                elif allowlist |> List.contains args then
                    do! db.UpsertBotSetting("LLM_DEPLOYMENT", args, "FREE_FORM", "llm")
                    do! settingsReloader.Reload()
                    return $"Модель переключена на {args} ✅", "model_switched"
                else
                    return
                        $"Такую модель не знаю и выдумывать не буду: «{args}». Выбирай из списка: {listText}",
                        "model_refused"
            })

    /// $-USD with 4 decimal places, invariant culture (never locale-dependent commas).
    let formatUsd (v: decimal) =
        "$" + v.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)

    /// Compact monospace-ish RU rendering of /usage's totals — no Markdown/parse_mode
    /// (avoids escaping model/display-name text), alignment via plain padding instead.
    let renderUsage
        (today: UsageTotalsRow)
        (week: UsageTotalsRow)
        (byModel: UsageByModelRow[])
        (byUser: UsageByUserRow[])
        : string =
        let header =
            [ "📊 Usage"
              ""
              $"Сегодня:  {today.calls,4} выз.  {formatUsd today.cost_usd}"
              $"7 дней:   {week.calls,4} выз.  {formatUsd week.cost_usd}" ]
        let modelLines =
            if byModel.Length = 0 then
                []
            else
                ""
                :: "По моделям (7 дней):"
                :: [ for m in byModel -> $"  {m.model,-24} {m.calls,4} выз.  {formatUsd m.cost_usd}" ]
        let userLines =
            if byUser.Length = 0 then
                []
            else
                ""
                :: "Топ пользователей (7 дней):"
                :: [ for u in byUser ->
                        let name = u.display_name |> Option.ofObj |> Option.defaultValue $"id{u.user_id}"
                        $"  {name,-24} {u.calls,4} выз.  {formatUsd u.cost_usd}" ]
        let footer = [ ""; $"Итого за 7 дней: {formatUsd week.cost_usd}" ]
        header @ modelLines @ userLines @ footer |> String.concat "\n"

    /// `/usage` — today + last 7 days totals, by-model and top-5-by-user breakdowns
    /// (7-day window), all read straight from `llm_usage` (see DbService).
    let handleUsageCommand (conf: BotConfiguration) (msg: Message) (from: User) (args: string) =
        handleSimpleCommand "usage" conf msg from args (fun () ->
            task {
                let now = time.GetUtcNow().UtcDateTime
                let todayStart = DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc)
                let weekStart = now.AddDays(-7.0)
                let! today = db.UsageTotals(todayStart)
                let! week = db.UsageTotals(weekStart)
                let! byModel = db.UsageByModel(weekStart)
                let! byUser = db.UsageByUser(weekStart, 5)
                return renderUsage today week byModel byUser, "usage_shown"
            })

    [<Literal>]
    let SummaryDefaultCount = 200

    [<Literal>]
    let SummaryMaxCount = 500

    /// `/summary [count]` arg parsing: a positive integer arg is capped at
    /// SummaryMaxCount; anything else (missing, non-numeric, <= 0) falls back to
    /// SummaryDefaultCount.
    let parseSummaryCount (args: string) =
        match Int32.TryParse(args.Trim()) with
        | true, v when v > 0 -> min v SummaryMaxCount
        | _ -> SummaryDefaultCount

    let summaryRequest (conf: BotConfiguration) (transcript: string) : ChatRequest =
        { Deployment = conf.LlmDeployment
          Messages =
            [ { Role = ChatRole.System
                Content = [ ContentPart.Text conf.SummaryPrompt ]
                ToolCalls = []
                ToolCallId = None }
              { Role = ChatRole.User
                Content = [ ContentPart.Text transcript ]
                ToolCalls = []
                ToolCallId = None } ]
          Tools = []
          Temperature = None
          MaxTokens = None }

    let buildTranscript (rows: MessageLogRow[]) : string =
        rows |> Array.map (fun r -> $"[{r.display_name}]: {r.text}") |> String.concat "\n"

    /// `/summary [count]` — speaker-attributed transcript of the last `count` (default
    /// SummaryDefaultCount, capped SummaryMaxCount) message_log rows for this chat, fed
    /// to a non-stream LLM call with the SUMMARY_PROMPT bot_setting, replied back.
    /// Ephemeral twist (Bot API 10.2 probe — see BotHelpers.trySendEphemeralOrReply and
    /// the "Ephemeral message probe" section of src/AlitaBot/README.md): the digest is
    /// sent visible only to the requester (`from.Id`) whenever Telegram accepts a
    /// receiver-scoped message for this chat, and falls back to a normal (whole-chat-
    /// visible) reply otherwise — permanently, per chat, for the rest of the process.
    let handleSummaryCommand (conf: BotConfiguration) (msg: Message) (from: User) (args: string) =
        task {
            use a = botActivity.StartActivity("handleSummaryCommand")
            %a.SetTag("chatId", msg.Chat.Id)
            %a.SetTag("fromId", from.Id)

            let! inserted =
                logAndEmbed conf (
                    logRow msg.Chat.Id msg.MessageId from.Id (Option.toObj from.Username) (displayNameOf from) from.IsBot
                        (match msg.ReplyToMessage with Some r -> Some r.MessageId | None -> None)
                        (if args = "" then "[summary-cmd]" else $"[summary-cmd] {args}"))

            let reply (text: string) (outcome: string) =
                task {
                    let! sent = BotHelpers.trySendEphemeralOrReply tg logger msg.Chat.Id from.Id text msg.MessageId
                    do! logAndEmbed conf (
                            logRow msg.Chat.Id sent.MessageId (botUserId conf) conf.BotUsername conf.BotUsername true
                                (Some msg.MessageId) text)
                        |> taskIgnore
                    %a.SetTag("outcome", outcome)
                    countOutcome outcome
                }

            if not inserted then
                %a.SetTag("outcome", "duplicate_update")
                countOutcome "duplicate_update"
            else
                let count = parseSummaryCount args
                let! rows = db.RecentContext(msg.Chat.Id, count)

                if rows.Length = 0 then
                    do! reply "Пока обсуждать нечего — история этого чата пуста." "summary_empty_history"
                else
                    let transcript = buildTranscript rows
                    let usageCtx: UsageContext = { ChatId = Some msg.Chat.Id; UserId = Some from.Id }

                    match! chat.Complete(summaryRequest conf transcript, usageCtx, CancellationToken.None) with
                    | Ok resp when not (String.IsNullOrWhiteSpace resp.Text) -> do! reply resp.Text "summary_generated"
                    | Ok _ -> do! reply "Модель промолчала — не смогла подвести итоги." "summary_empty_response"
                    | Error err ->
                        logger.LogWarning("Summary generation failed: {Error}", string err)
                        do! reply "Не получилось подвести итоги 🙁" "summary_failed"
        }

    // ── /ask (Slice 5a: semantic search over this chat's message_embedding) ────

    /// Context block fed to the LLM: one line per matched message, oldest first
    /// (matches DbService.SemanticSearch's ordering) — author, date, quoted text.
    let buildAskContext (rows: AskMatchRow[]) : string =
        rows
        |> Array.map (fun r -> $"""[{r.display_name}, {r.sent_at.ToString("yyyy-MM-dd")}]: {r.text}""")
        |> String.concat "\n"

    let askRequest (conf: BotConfiguration) (question: string) (context: string) : ChatRequest =
        { Deployment = conf.LlmDeployment
          Messages =
            [ { Role = ChatRole.System
                Content = [ ContentPart.Text conf.AskPrompt ]
                ToolCalls = []
                ToolCallId = None }
              { Role = ChatRole.User
                Content = [ ContentPart.Text $"Вопрос: {question}\n\nСообщения из истории чата:\n{context}" ]
                ToolCalls = []
                ToolCallId = None } ]
          Tools = []
          Temperature = None
          MaxTokens = None }

    /// `/ask <question>` — embeds the question, pulls the ASK_TOP_K nearest
    /// message_embedding rows for this chat above ASK_SIM_FLOOR cosine similarity
    /// (DbService.SemanticSearch), and answers via a non-stream LLM call grounded in
    /// that context (ASK_PROMPT). No candidates above the floor short-circuits to a
    /// fixed RU "nothing relevant" reply — deterministic, no LLM call needed to say
    /// there's nothing to say. Empty question -> RU usage hint, no embedding/LLM call.
    let handleAskCommand (conf: BotConfiguration) (msg: Message) (from: User) (question: string) =
        task {
            use a = botActivity.StartActivity("handleAskCommand")
            %a.SetTag("chatId", msg.Chat.Id)
            %a.SetTag("fromId", from.Id)

            let! inserted =
                logAndEmbed conf (
                    logRow msg.Chat.Id msg.MessageId from.Id (Option.toObj from.Username) (displayNameOf from) from.IsBot
                        (match msg.ReplyToMessage with Some r -> Some r.MessageId | None -> None)
                        (if question = "" then "[ask-cmd]" else $"[ask-cmd] {question}"))

            let reply (text: string) (outcome: string) =
                task {
                    let! sent = BotHelpers.sendTextReply tg msg.Chat.Id text msg.MessageId
                    do! logAndEmbed conf (
                            logRow msg.Chat.Id sent.MessageId (botUserId conf) conf.BotUsername conf.BotUsername true
                                (Some msg.MessageId) text)
                        |> taskIgnore
                    %a.SetTag("outcome", outcome)
                    countOutcome outcome
                }

            if not inserted then
                %a.SetTag("outcome", "duplicate_update")
                countOutcome "duplicate_update"
            elif String.IsNullOrWhiteSpace question then
                do! reply "Спроси что-нибудь про этот чат: `/ask когда договорились встретиться`." "ask_empty_question"
            else
                let usageCtx: UsageContext = { ChatId = Some msg.Chat.Id; UserId = Some from.Id }

                match! embeddings.Embed(conf.EmbeddingDeployment, [ question ], usageCtx, CancellationToken.None) with
                | Error err ->
                    logger.LogWarning("/ask: failed to embed the question: {Error}", string err)
                    do! reply "Не получилось разобрать вопрос 🙁" "ask_embed_failed"
                | Ok vectors when vectors.Length = 0 || vectors[0].Length = 0 ->
                    logger.LogWarning("/ask: embedding returned no vectors for the question")
                    do! reply "Не получилось разобрать вопрос 🙁" "ask_embed_failed"
                | Ok vectors ->
                    let! matches = db.SemanticSearch(msg.Chat.Id, vectors[0], conf.AskTopK, conf.AskSimFloor)

                    if matches.Length = 0 then
                        do! reply "Ничего подходящего в истории этого чата не нашла." "ask_no_matches"
                    else
                        let context = buildAskContext matches

                        match! chat.Complete(askRequest conf question context, usageCtx, CancellationToken.None) with
                        | Ok resp when not (String.IsNullOrWhiteSpace resp.Text) -> do! reply resp.Text "ask_answered"
                        | Ok _ -> do! reply "Модель промолчала." "ask_empty_response"
                        | Error err ->
                            logger.LogWarning("/ask: LLM call failed: {Error}", string err)
                            do! reply "Не получилось ответить 🙁" "ask_failed"
        }

    // ── /dossier, /forget-me (Slice 5b: per-person dossiers) ───────────────────

    /// Fixed RU reply for "no dossier yet" — used both for a resolved user with no
    /// person_dossier row, and for an unresolvable `@username`. Deliberately the same
    /// message for both: distinguishing "never mentioned" from "unknown username" would
    /// leak whether a given username has ever posted in the chat.
    [<Literal>]
    let NoDossierText = "пусто, я тебя ещё не изучила"

    /// Renders a dossier's summary plus its newest `/dossier`-visible facts (already
    /// fetched — DbService.NewestActiveFacts), newest first.
    let renderDossier (summary: string) (facts: ActiveFactRow[]) : string =
        if facts.Length = 0 then
            summary
        else
            let factsText = facts |> Array.map (fun f -> $"- {f.content}") |> String.concat "\n"
            $"{summary}\n\nФакты:\n{factsText}"

    [<Literal>]
    let DossierFactsShown = 5

    /// `/dossier` (self, no arg) or `/dossier @username` (another chat member, `@`
    /// optional) — renders the person's cumulative summary plus their newest
    /// DossierFactsShown active facts, or NoDossierText when there's nothing yet (unknown
    /// username, or a known user with no dossier row).
    let handleDossierCommand (conf: BotConfiguration) (msg: Message) (from: User) (args: string) =
        handleSimpleCommand "dossier" conf msg from args (fun () ->
            task {
                let trimmedArg = args.Trim().TrimStart('@')
                let! targetIdOpt =
                    if trimmedArg = "" then Task.FromResult(Some from.Id) else db.ResolveUserIdByUsername(trimmedArg)

                match targetIdOpt with
                | None -> return NoDossierText, "dossier_unknown_user"
                | Some targetId ->
                    match! db.GetPersonDossier(targetId) with
                    | None -> return NoDossierText, "dossier_empty"
                    | Some dossier ->
                        let! facts = db.NewestActiveFacts(targetId, DossierFactsShown)
                        return renderDossier dossier.summary facts, "dossier_shown"
            })

    /// `/forget-me` — opts the requester out of memory (memory_opt_out), hard-deletes
    /// their interaction_memory/person_dossier/message_embedding rows (DbService.
    /// PurgeUserMemory), and confirms. message_log itself is untouched — it's the shared
    /// chat record, not personal memory (see the V4 migration). From this point on the
    /// requester is excluded from the nightly dossier job, the inline embedding pipeline,
    /// and recall injection (ResponderService) — see DossierService/BotService.tryEmbed.
    let handleForgetMeCommand (conf: BotConfiguration) (msg: Message) (from: User) (args: string) =
        handleSimpleCommand "forget-me" conf msg from args (fun () ->
            task {
                do! db.OptOutUser(from.Id)
                do! db.PurgeUserMemory(from.Id)
                return
                    "Забыла всё, что успела про тебя узнать, и больше не буду запоминать. "
                    + "История сообщений в чате (message_log) не трогается — это общий архив чата, а не личные данные.",
                    "forget_me_done"
            })

    // ── /roast, /awards, /quote, /karma (Slice 7: social engine) ───────────────
    //
    // /roast and /awards deliver their reply via Mdv2Delivery (MarkdownV2, same pipeline
    // the LLM responder's own renderers use) — the LLM output is free text that may
    // legitimately contain markdown; /quote and /karma render fixed/templated RU text
    // with no markdown-sensitive content, so they stay on handleSimpleCommand's plain
    // sendTextReply like /usage, /dossier, /forget-me.

    /// Calls `chat.Complete(request, ...)` and applies `tryParse` to the raw response
    /// text; on an LLM failure OR a malformed/unparseable response, retries ONCE with the
    /// exact same request before giving up. `/awards`/`/quote` both need a strict JSON
    /// shape out of a free-text-capable model — this is the one retry the plan asks for,
    /// shared so both commands get identical behavior. Returns `None` only after both
    /// attempts fail — callers render a fixed RU failure reply in that case, never crash
    /// or leave a half-written state (no karma rows / no quote) behind.
    let completeJsonWithRetry (usageCtx: UsageContext) (request: ChatRequest) (tryParse: string -> 'a option) : Task<'a option> =
        task {
            let attempt () =
                task {
                    match! chat.Complete(request, usageCtx, CancellationToken.None) with
                    | Ok resp -> return tryParse resp.Text
                    | Error err ->
                        logger.LogWarning("Social JSON LLM call failed: {Error}", string err)
                        return None
                }
            match! attempt () with
            | Some v -> return Some v
            | None ->
                logger.LogWarning("Social JSON LLM response malformed or the call failed — retrying once")
                return! attempt ()
        }

    /// The handle a `/awards`/`/quote` transcript line attributes a message to: `@username`
    /// when the sender has one, their `display_name` otherwise (not everyone sets a
    /// Telegram username) — also what `/awards` asks the LLM to echo back verbatim in its
    /// "user" field, so `handleAwardsCommand` can try to resolve it against
    /// `message_log.username` afterwards.
    let socialHandleOf (r: MessageLogRow) =
        if String.IsNullOrWhiteSpace(r.username: string) then r.display_name else $"@{r.username}"

    let buildHandleTranscript (rows: MessageLogRow[]) : string =
        rows |> Array.map (fun r -> $"[{socialHandleOf r}]: {r.text}") |> String.concat "\n"

    /// Defensive cleanup for a `/awards` "user" field: despite the prompt explicitly
    /// asking for the handle WITHOUT its surrounding `[...]`, a real model (confirmed
    /// against Azure AI Foundry in `SocialRealTests.fs`) sometimes echoes the bracketed
    /// form verbatim from the transcript line (e.g. `"[Ayrat Ru]"` instead of `"Ayrat Ru"`)
    /// — stripped here so both the `@username` resolution check and the rendered
    /// announcement/stored `karma.username` are never left with stray brackets.
    let stripUserHandleBrackets (s: string) =
        let t = s.Trim()
        if t.Length >= 2 && t.StartsWith "[" && t.EndsWith "]" then t.Substring(1, t.Length - 2).Trim() else t

    // ── /roast ───────────────────────────────────────────────────────────────

    [<Literal>]
    let RoastMessagesLimit = 50

    [<Literal>]
    let RoastFactsK = 8

    [<Literal>]
    let NoRoastDataText = "этого кадра я ещё не изучила"

    [<Literal>]
    let RoastCooldownText = "этого уже жарили, дай остыть"

    /// Resolves `/roast`'s target: an explicit `@username`/`username` arg (message_log
    /// lookup for user_id + display_name — `None` when nobody with that username has ever
    /// been logged, treated the same as "no data" by the caller); otherwise the message
    /// being replied to's author; otherwise the invoker themselves.
    let resolveRoastTarget (msg: Message) (from: User) (args: string) : Task<RoastTarget option> =
        task {
            let trimmed = args.Trim().TrimStart('@')
            if trimmed <> "" then
                match! db.ResolveUserByUsername(trimmed) with
                | Some(uid, name) -> return Some { UserId = uid; DisplayName = name }
                | None -> return None
            else
                match msg.ReplyToMessage |> Option.bind (fun r -> r.From) with
                | Some u -> return Some { UserId = u.Id; DisplayName = displayNameOf u }
                | None -> return Some { UserId = from.Id; DisplayName = displayNameOf from }
        }

    let roastAmmoIsEmpty (ammo: RoastAmmo) =
        ammo.DossierSummary.IsNone && ammo.Facts.IsEmpty && ammo.Messages.IsEmpty

    /// Gathers `/roast`'s ammunition for `target`: dossier summary + up to RoastFactsK
    /// newest active interaction_memory facts (no similarity filter — same "just take the
    /// newest" posture as `/dossier`'s NewestActiveFacts, unlike ResponderService's
    /// similarity-scored recall) + up to RoastMessagesLimit of the target's own recent
    /// message_log texts. An opted-out (`memory_opt_out`) target gets roasted ONLY from
    /// their recent messages — dossier/facts are never read for them, respecting the same
    /// boundary `/forget-me` established elsewhere in the bot.
    let gatherRoastAmmo (target: RoastTarget) : Task<RoastAmmo> =
        task {
            let! optedOut = db.IsOptedOut(target.UserId)
            let! recentRows = db.UserRecentMessages(target.UserId, RoastMessagesLimit)
            let messages = recentRows |> Array.map (fun r -> r.text) |> Array.toList
            if optedOut then
                return { DossierSummary = None; Facts = []; Messages = messages }
            else
                let! dossierOpt = db.GetPersonDossier(target.UserId)
                let! facts = db.NewestActiveFacts(target.UserId, RoastFactsK)
                return
                    { DossierSummary = dossierOpt |> Option.map (fun d -> d.summary)
                      Facts = facts |> Array.map (fun f -> f.content) |> Array.toList
                      Messages = messages }
        }

    let roastRequest (conf: BotConfiguration) (targetName: string) (ammo: RoastAmmo) : ChatRequest =
        let parts =
            [ ammo.DossierSummary |> Option.map (fun s -> $"Досье:\n{s}")
              (if ammo.Facts.IsEmpty then
                   None
               else
                   Some("Факты:\n" + (ammo.Facts |> List.map (fun f -> $"- {f}") |> String.concat "\n")))
              (if ammo.Messages.IsEmpty then
                   None
               else
                   Some("Сообщения:\n" + (ammo.Messages |> List.map (fun m -> $"- {m}") |> String.concat "\n"))) ]
            |> List.choose id
        let body = String.concat "\n\n" parts
        { Deployment = conf.LlmDeployment
          Messages =
            [ { Role = ChatRole.System
                Content = [ ContentPart.Text conf.RoastPrompt ]
                ToolCalls = []
                ToolCallId = None }
              { Role = ChatRole.User
                Content = [ ContentPart.Text $"Цель: {targetName}\n\n{body}" ]
                ToolCalls = []
                ToolCallId = None } ]
          Tools = []
          Temperature = None
          MaxTokens = None }

    /// `/roast [@username | reply-to-target]` — see `resolveRoastTarget`/`gatherRoastAmmo`
    /// for target resolution and ammunition gathering. Delivered via `Mdv2Delivery.sendFinal`
    /// (non-stream LLM call -> MDV2 render -> reply), `ROAST_COOLDOWN_SECONDS` (default 300)
    /// per target — a fresh roast only stamps `roast_cooldown` on an actual successful
    /// delivery, never on a cooldown/no-data/failed attempt.
    let handleRoastCommand (conf: BotConfiguration) (msg: Message) (from: User) (args: string) =
        task {
            use a = botActivity.StartActivity("handleRoastCommand")
            %a.SetTag("chatId", msg.Chat.Id)
            %a.SetTag("fromId", from.Id)

            let! inserted =
                logAndEmbed conf (
                    logRow msg.Chat.Id msg.MessageId from.Id (Option.toObj from.Username) (displayNameOf from) from.IsBot
                        (match msg.ReplyToMessage with Some r -> Some r.MessageId | None -> None)
                        (if args = "" then "[roast-cmd]" else $"[roast-cmd] {args}"))

            let reply (text: string) (outcome: string) =
                task {
                    let! sent = Mdv2Delivery.sendFinal tg logger msg.Chat.Id msg.MessageId text
                    do! logAndEmbed conf (
                            logRow msg.Chat.Id sent.MessageId (botUserId conf) conf.BotUsername conf.BotUsername true
                                (Some msg.MessageId) text)
                        |> taskIgnore
                    %a.SetTag("outcome", outcome)
                    countOutcome outcome
                }

            if not inserted then
                %a.SetTag("outcome", "duplicate_update")
                countOutcome "duplicate_update"
            else
                match! resolveRoastTarget msg from args with
                | None -> do! reply NoRoastDataText "roast_unknown_target"
                | Some target ->
                    let now = time.GetUtcNow().UtcDateTime
                    let! lastRoasted = db.LastRoastedAt(target.UserId)
                    match lastRoasted with
                    | Some last when (now - last).TotalSeconds < float conf.RoastCooldownSeconds ->
                        do! reply RoastCooldownText "roast_cooldown"
                    | _ ->
                        let! ammo = gatherRoastAmmo target
                        if roastAmmoIsEmpty ammo then
                            do! reply NoRoastDataText "roast_no_data"
                        else
                            let usageCtx: UsageContext = { ChatId = Some msg.Chat.Id; UserId = Some from.Id }
                            match! chat.Complete(roastRequest conf target.DisplayName ammo, usageCtx, CancellationToken.None) with
                            | Ok resp when not (String.IsNullOrWhiteSpace resp.Text) ->
                                do! db.RecordRoast(target.UserId, now)
                                do! reply resp.Text "roast_delivered"
                            | Ok _ -> do! reply "Слов не нашлось — то ещё достижение." "roast_empty_response"
                            | Error err ->
                                logger.LogWarning("Roast generation failed: {Error}", string err)
                                do! reply "Не получилось прожарить 🙁" "roast_failed"
        }

    // ── /awards ──────────────────────────────────────────────────────────────

    [<Literal>]
    let AwardsWindowDays = 7.0

    [<Literal>]
    let AwardsTranscriptCap = 800

    /// Lenient-but-strict parse of the awards LLM's JSON contract: an array of
    /// `{title, user, evidence_quote}` objects, all three string fields required and
    /// non-blank for title/user. Unlike `DossierService.parseFactsJson` (which silently
    /// drops malformed elements), a single malformed entry fails the WHOLE parse — the
    /// plan's "malformed JSON after 1 retry -> graceful failure" applies to the response
    /// as a whole, not per-entry, so `completeJsonWithRetry` retries the entire call
    /// rather than shipping a partially-understood awards list.
    let parseAwardsJson (json: string) : AwardEntry list option =
        try
            use doc = JsonDocument.Parse(json: string)
            if doc.RootElement.ValueKind <> JsonValueKind.Array then
                None
            else
                let elements = doc.RootElement.EnumerateArray() |> Seq.toList
                if elements.IsEmpty then
                    None
                else
                    let parsed =
                        elements
                        |> List.map (fun el ->
                            if el.ValueKind <> JsonValueKind.Object then
                                None
                            else
                                match el.TryGetProperty "title", el.TryGetProperty "user", el.TryGetProperty "evidence_quote" with
                                | (true, t), (true, u), (true, e) when
                                    t.ValueKind = JsonValueKind.String
                                    && u.ValueKind = JsonValueKind.String
                                    && e.ValueKind = JsonValueKind.String
                                    && not (String.IsNullOrWhiteSpace(t.GetString()))
                                    && not (String.IsNullOrWhiteSpace(u.GetString())) ->
                                    Some
                                        { Title = t.GetString()
                                          User = stripUserHandleBrackets (u.GetString())
                                          EvidenceQuote = e.GetString() }
                                | _ -> None)
                    if parsed |> List.exists Option.isNone then None else Some(parsed |> List.choose id)
        with _ ->
            None

    let awardsRequest (conf: BotConfiguration) (transcript: string) : ChatRequest =
        { Deployment = conf.LlmDeployment
          Messages =
            [ { Role = ChatRole.System
                Content = [ ContentPart.Text conf.AwardsPrompt ]
                ToolCalls = []
                ToolCallId = None }
              { Role = ChatRole.User
                Content = [ ContentPart.Text transcript ]
                ToolCalls = []
                ToolCallId = None } ]
          Tools = []
          Temperature = None
          MaxTokens = None }

    let renderAwards (awards: AwardEntry list) : string =
        let lines = awards |> List.map (fun a -> $"🏆 {a.Title} — {a.User}: „{a.EvidenceQuote}\"")
        "Награды недели:\n" + String.concat "\n" lines

    /// `/awards` — over the last AwardsWindowDays (7) of this chat's message_log (capped
    /// AwardsTranscriptCap (~800) rows, human messages only — see `HumanMessagesSince`),
    /// the AWARDS_PROMPT LLM call returns a strict JSON array of 3-5 witty
    /// {title, user, evidence_quote} awards, rendered as one line each and written to
    /// `karma` (user_id resolved from message_log.username when the LLM's "user" field is
    /// a "@handle" it can match — see `socialHandleOf`; kept unresolved otherwise rather
    /// than dropped). Malformed JSON after one retry -> a fixed RU failure reply, no karma
    /// rows written, never a crash.
    let handleAwardsCommand (conf: BotConfiguration) (msg: Message) (from: User) (args: string) =
        task {
            use a = botActivity.StartActivity("handleAwardsCommand")
            %a.SetTag("chatId", msg.Chat.Id)
            %a.SetTag("fromId", from.Id)

            let! inserted =
                logAndEmbed conf (
                    logRow msg.Chat.Id msg.MessageId from.Id (Option.toObj from.Username) (displayNameOf from) from.IsBot
                        (match msg.ReplyToMessage with Some r -> Some r.MessageId | None -> None)
                        (if args = "" then "[awards-cmd]" else $"[awards-cmd] {args}"))

            let reply (text: string) (outcome: string) =
                task {
                    let! sent = Mdv2Delivery.sendFinal tg logger msg.Chat.Id msg.MessageId text
                    do! logAndEmbed conf (
                            logRow msg.Chat.Id sent.MessageId (botUserId conf) conf.BotUsername conf.BotUsername true
                                (Some msg.MessageId) text)
                        |> taskIgnore
                    %a.SetTag("outcome", outcome)
                    countOutcome outcome
                }

            if not inserted then
                %a.SetTag("outcome", "duplicate_update")
                countOutcome "duplicate_update"
            else
                let since = time.GetUtcNow().UtcDateTime.AddDays(-AwardsWindowDays)
                let! rows = db.HumanMessagesSince(msg.Chat.Id, since, AwardsTranscriptCap)

                if rows.Length = 0 then
                    do! reply "Не за что вручать — эта неделя прошла на редкость тихо." "awards_empty"
                else
                    let transcript = buildHandleTranscript rows
                    let usageCtx: UsageContext = { ChatId = Some msg.Chat.Id; UserId = Some from.Id }

                    match! completeJsonWithRetry usageCtx (awardsRequest conf transcript) parseAwardsJson with
                    | None -> do! reply "Не получилось раздать награды 🙁" "awards_failed"
                    | Some awards when awards.IsEmpty -> do! reply "Не получилось раздать награды 🙁" "awards_failed"
                    | Some awards ->
                        for aw in awards do
                            let! resolvedId =
                                if aw.User.StartsWith "@" then db.ResolveUserIdByUsername(aw.User.TrimStart '@')
                                else Task.FromResult None
                            do! db.InsertKarmaAward(resolvedId, aw.User, aw.Title, aw.EvidenceQuote)
                        do! reply (renderAwards awards) "awards_delivered"
        }

    // ── /quote ───────────────────────────────────────────────────────────────

    [<Literal>]
    let QuoteWindowHours = 24.0

    [<Literal>]
    let QuoteTranscriptCap = 500

    let parseQuoteJson (json: string) : QuoteEntry option =
        try
            use doc = JsonDocument.Parse(json: string)
            if doc.RootElement.ValueKind <> JsonValueKind.Object then
                None
            else
                match
                    doc.RootElement.TryGetProperty "author", doc.RootElement.TryGetProperty "quote", doc.RootElement.TryGetProperty "comment"
                with
                | (true, a), (true, q), (true, c) when
                    a.ValueKind = JsonValueKind.String
                    && q.ValueKind = JsonValueKind.String
                    && c.ValueKind = JsonValueKind.String
                    && not (String.IsNullOrWhiteSpace(q.GetString())) ->
                    Some { Author = a.GetString(); Quote = q.GetString(); Comment = c.GetString() }
                | _ -> None
        with _ ->
            None

    let quoteRequest (conf: BotConfiguration) (transcript: string) : ChatRequest =
        { Deployment = conf.LlmDeployment
          Messages =
            [ { Role = ChatRole.System
                Content = [ ContentPart.Text conf.QuotePrompt ]
                ToolCalls = []
                ToolCallId = None }
              { Role = ChatRole.User
                Content = [ ContentPart.Text transcript ]
                ToolCalls = []
                ToolCallId = None } ]
          Tools = []
          Temperature = None
          MaxTokens = None }

    let renderQuote (q: QuoteEntry) : string =
        $"💬 Цитата дня: „{q.Quote}\" — {q.Author}. {q.Comment}"

    /// `/quote` — the last QuoteWindowHours (24) of this chat's human, non-command
    /// message_log rows (capped QuoteTranscriptCap (~500) — see `HumanMessagesSince`)
    /// feed the QUOTE_PROMPT LLM call, which must answer with a strict JSON
    /// `{author, quote, comment}` picking the single most absurd/quotable line. No
    /// messages in the window -> a fixed RU "nothing to quote" reply, no LLM call.
    let handleQuoteCommand (conf: BotConfiguration) (msg: Message) (from: User) (args: string) =
        handleSimpleCommand "quote" conf msg from args (fun () ->
            task {
                let since = time.GetUtcNow().UtcDateTime.AddHours(-QuoteWindowHours)
                let! rows = db.HumanMessagesSince(msg.Chat.Id, since, QuoteTranscriptCap)

                if rows.Length = 0 then
                    return "За последние сутки цитировать особо некого.", "quote_empty"
                else
                    let transcript = buildHandleTranscript rows
                    let usageCtx: UsageContext = { ChatId = Some msg.Chat.Id; UserId = Some from.Id }

                    match! completeJsonWithRetry usageCtx (quoteRequest conf transcript) parseQuoteJson with
                    | Some q -> return renderQuote q, "quote_generated"
                    | None -> return "Не получилось выбрать цитату дня 🙁", "quote_failed"
            })

    // ── /karma ───────────────────────────────────────────────────────────────

    [<Literal>]
    let KarmaRecentTitles = 3

    [<Literal>]
    let NoKarmaText = "пока без наград"

    let renderKarma (count: int64) (titles: string[]) : string =
        let titlesText = titles |> Array.map (fun t -> $"- {t}") |> String.concat "\n"
        $"Наград: {count}\nПоследние:\n{titlesText}"

    /// `/karma [@user]` — self (no arg) or another chat member's totals from `karma`: a
    /// count plus the newest KarmaRecentTitles (3) titles. An unresolvable `@username` or
    /// a known user with zero karma rows both render the same fixed "no awards yet" reply
    /// (same "don't leak who's ever been seen" posture as `/dossier`'s NoDossierText).
    let handleKarmaCommand (conf: BotConfiguration) (msg: Message) (from: User) (args: string) =
        handleSimpleCommand "karma" conf msg from args (fun () ->
            task {
                let trimmedArg = args.Trim().TrimStart('@')
                let! targetIdOpt =
                    if trimmedArg = "" then Task.FromResult(Some from.Id) else db.ResolveUserIdByUsername(trimmedArg)

                match targetIdOpt with
                | None -> return NoKarmaText, "karma_unknown_user"
                | Some targetId ->
                    let! count = db.KarmaCount(targetId)
                    if count = 0L then
                        return NoKarmaText, "karma_empty"
                    else
                        let! titles = db.KarmaNewestTitles(targetId, KarmaRecentTitles)
                        return renderKarma count titles, "karma_shown"
            })

    let commandsWithoutHelp: CommandDef list =
        [ { Name = "img"
            Aliases = []
            Description = "сгенерировать картинку по описанию (ответом на фото — перерисовать его)"
            Handler = handleImageCommand }
          { Name = "model"
            Aliases = []
            Description = "показать текущую LLM-модель, или переключить: /model <имя>"
            Handler = handleModelCommand }
          { Name = "summary"
            Aliases = [ "tldr" ]
            Description =
              $"итоги последних N сообщений чата (по умолчанию {SummaryDefaultCount}, максимум {SummaryMaxCount}): /summary [N]"
            Handler = handleSummaryCommand }
          { Name = "usage"
            Aliases = []
            Description = "расход и статистика использования LLM"
            Handler = handleUsageCommand }
          { Name = "ask"
            Aliases = []
            Description = "ответить на вопрос по истории этого чата (семантический поиск): /ask <вопрос>"
            Handler = handleAskCommand }
          { Name = "dossier"
            Aliases = []
            Description = "досье: своё (без аргумента) или чужое — /dossier @username"
            Handler = handleDossierCommand }
          { Name = "forget-me"
            Aliases = []
            Description = "забыть всё личное — выйти из системы памяти и удалить накопленное досье"
            Handler = handleForgetMeCommand }
          { Name = "roast"
            Aliases = []
            Description = "прожарить: /roast @username, ответом на сообщение, или без аргумента — себя"
            Handler = handleRoastCommand }
          { Name = "awards"
            Aliases = []
            Description = "награды недели по итогам последних 7 дней в чате"
            Handler = handleAwardsCommand }
          { Name = "quote"
            Aliases = []
            Description = "цитата дня — самая абсурдная реплика за последние 24 часа"
            Handler = handleQuoteCommand }
          { Name = "karma"
            Aliases = []
            Description = "карма: своя (без аргумента) или чужая — /karma @username"
            Handler = handleKarmaCommand } ]

    /// `/help` (and `/start`, for a newcomer's first message) — auto-generated from the
    /// registry (Commands.helpText), so it can never list a command that doesn't exist
    /// or omit one that does.
    let handleHelpCommand (conf: BotConfiguration) (msg: Message) (from: User) (args: string) =
        handleSimpleCommand "help" conf msg from args (fun () ->
            task {
                let displayDefs =
                    commandsWithoutHelp
                    @ [ { Name = "help"
                          Aliases = [ "start" ]
                          Description = "список команд"
                          Handler = fun _ _ _ _ -> Task.FromResult(()) } ]
                return Commands.helpText displayDefs, "help_shown"
            })

    let commands: CommandDef list =
        commandsWithoutHelp
        @ [ { Name = "help"
              Aliases = [ "start" ]
              Description = "список команд"
              Handler = handleHelpCommand } ]

    member _.OnUpdate(update: Update) =
        task {
            let conf = options.Value
            match update.Message with
            | Some msg ->
                match msg.From with
                | Some from ->
                    // Serializes everything below per chat: two rapid triggers (or a
                    // webhook retry racing the original attempt) in the same chat no
                    // longer run concurrently — see withChatLock. Different chats still
                    // process fully in parallel.
                    do!
                        withChatLock msg.Chat.Id (fun () ->
                            task {
                                match msg.Text with
                                | Some text ->
                                    if conf.TargetChatIds |> List.contains msg.Chat.Id then
                                        match Commands.tryMatch conf commands text with
                                        | Some(cmdDef, args) ->
                                            Metrics.commandTotal.Add(1L, KeyValuePair("command", box cmdDef.Name))
                                            do! cmdDef.Handler conf msg from args
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
                            })
                | None -> ()
            | None -> ()
        }
