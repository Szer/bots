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

// ── Module-level shapes still local to BotService ────────────────────────────
//
// Module-level (not nested in BotService) — F# doesn't support type declarations inside
// a class's `let`-bound implementation section, only at module/namespace scope.
// RoastTarget/RoastAmmo/AwardEntry/QuoteEntry/LlmModelEntry moved to CommandCores.fs (S10
// PR2 prerequisite, mirroring PR1's MessageLog/Admin/MediaActions extraction) — their
// cores are now shared with the read-only NL tools; see CommandCores.fs's header comment.

/// The meme-react vision LLM's strict JSON contract (Slice 8) — see
/// `BotService.parseMemeJson`. Missing `emoji`/`text` fields default to "" (only `action`
/// is required to parse at all, since only one of the two ever matters per action).
type MemeAction = { Action: string; Emoji: string; Text: string }

/// `/say`'s optional leading voice-name token, resolved against `validTtsVoices` (Slice 9
/// stretch) — module-level for the same reason as MemeAction above. Command-syntax
/// parsing only (S10 PR2: the `speak_text` NL tool takes an explicit `voice` argument
/// instead of this ambiguous first-token sniffing), so it stays here, not CommandCores.
[<RequireQualifiedAccess>]
type SayVoiceArg =
    /// No voice token present — use TTS_DEFAULT_VOICE.
    | NoVoice
    /// A recognized voice name.
    | Valid of string
    /// A single leftover token that isn't a recognized voice name — only reported when
    /// there's no other plausible reading (see BotService.parseSayArgs).
    | Invalid of string

type BotService(
    options: IOptions<BotConfiguration>,
    db: DbService,
    responder: ResponderService,
    tg: ITelegramApi,
    speech: ISpeech,
    chat: IChatCompletion,
    imageGen: IImageGen,
    musicGen: IMusicGen,
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

    /// Bot user id is the numeric prefix of the bot token — moved to `BotHelpers` (Slice 8:
    /// `DigestService` needs it too) and aliased here so every existing `botUserId conf`
    /// call site in this file is untouched.
    let botUserId = BotHelpers.botUserId

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

    // ── message_log bookkeeping (Slice 5a: memory foundation) — extracted to
    // Services/MessageLog.fs (S10 PR1 prerequisite) so the NL tool-calling loop's
    // ToolExecutorService can log its own media-tool replies the same way. Rebound locally
    // so every existing ~30-site call (logRow chatId messageId ...; logAndEmbed conf row)
    // stays byte-for-byte unchanged.
    let logRow = MessageLog.logRow time
    let logAndEmbed = MessageLog.logAndEmbed logger embeddings db

    /// Command "cores" — extracted to Services/CommandCores.fs (S10 PR2 prerequisite) so
    /// the read-only NL tools (ask_chat_history, summarize_chat, show_dossier, roast_user,
    /// show_awards, show_quote, show_karma, switch_model, show_usage) share the EXACT SAME
    /// DB-read + LLM-call logic every command handler below uses — rebound locally with
    /// this file's own `db`/`chat`/`logger`/`time`/`embeddings` so every call site below
    /// reads like a direct call, same convention as logRow/logAndEmbed above.
    let askCore = CommandCores.askCore db embeddings chat logger
    let summaryCore = CommandCores.summaryCore db chat logger
    let dossierCore = CommandCores.dossierCore db
    let roastCommandCore = CommandCores.roastCommandCore db chat logger time
    let awardsCore = CommandCores.awardsCore db chat logger time
    let quoteCore = CommandCores.quoteCore db chat logger time
    let karmaCore = CommandCores.karmaCore db
    let modelCore = CommandCores.modelCore db settingsReloader
    let usageCore = CommandCores.usageCore db time
    let sqlCore = CommandCores.sqlCore db chat logger
    let buildTranscript = CommandCores.buildTranscript

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
          MaxTokens = Some 60
          ReasoningEffort = None }

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

    // ── Reaction channel (redesign, PR #253 follow-up) ───────────────────────
    //
    // Emoji reactions are now an INDEPENDENT channel, not a coin-flip competing with
    // replying. A TRIGGERED message (mention, reply-to-bot, name trigger) always gets the
    // reply path — see `isTriggered`/`handleTriggerableMessage` below; the old
    // OUTCOME_WEIGHTS reply/silence/emoji roll is gone. Separately, REACTION_PROBABILITY
    // rolls on EVERY first-delivery message in a target chat (triggered or not),
    // REACTION_COOLDOWN_SECONDS-gated per chat — see `tryReact` further down. A message
    // can get both a reaction AND a reply; the two never interfere with each other.

    /// REACTION_PALETTE (bot_setting, hot-reloadable), validated against Telegram's
    /// allowed reaction-emoji set (`OutcomeRouter.parsePalette`) — shared by the
    /// message-level reaction roll (`tryReact`) and the S8 meme-react "react" action so
    /// both draw from (and validate against) the exact same tunable palette. Parsed fresh
    /// on every call (cheap: a handful of emoji) so a `/reload-settings` change takes
    /// effect immediately, same as every other bot_setting; entries dropped for not being
    /// on Telegram's list are Warning-logged.
    let reactionPalette (conf: BotConfiguration) : string[] =
        let palette, invalid = OutcomeRouter.parsePalette conf.ReactionPaletteJson
        if not invalid.IsEmpty then
            logger.LogWarning(
                "REACTION_PALETTE: entries not in Telegram's allowed reaction set were dropped: {Invalid}",
                String.concat " " invalid)
        palette

    /// Shared by the S6 emoji outcome and the S8 meme-react "react" action:
    /// `Req.SetMessageReaction` with a single emoji. Best-effort — a rejected call is
    /// Warning-logged (tagged with `context`) and swallowed, never blocking/retrying the
    /// triggering message. Returns whether the call succeeded so callers that count
    /// outcomes (meme-react) can distinguish a real reaction from a failed one.
    let setReaction (context: string) (msg: Message) (emoji: string) : Task<bool> =
        task {
            let reaction = [| ReactionType.Emoji(ReactionTypeEmoji.Create(``type`` = "emoji", emoji = emoji)) |]
            try
                do! tg.CallExn(Req.SetMessageReaction.Make(msg.Chat.Id, msg.MessageId, reaction = reaction)) |> taskIgnore
                return true
            with ex ->
                logger.LogWarning(ex, "{Context}: SetMessageReaction failed for message {MessageId}", context, msg.MessageId)
                return false
        }

    let emojiPickRequest (conf: BotConfiguration) (palette: string[]) (text: string) : ChatRequest =
        let allowedText = String.concat "" palette
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
          MaxTokens = Some 10
          // Same cheap-formulaic-ask lever MediaActions.composeCaption uses (S10 prod
          // incident) — this system prompt is a single short line (never conf.SystemPrompt's
          // full persona), so reasoning-token exhaustion was never observed here, but there's
          // no reason to pay for reasoning on a single-emoji pick either.
          ReasoningEffort = Some "minimal" }

    /// Picks ONE emoji from `palette` for a reaction. REACTION_CHOICE_MODE gates HOW:
    /// "random" (cheapest) skips the LLM entirely; "llm" (default) makes a tiny non-stream
    /// LLM call asking the model to pick the best-fitting emoji for the message text. A
    /// failed LLM call, or an answer outside the palette, falls back to a uniform random
    /// pick — never a fixed emoji, and never a refusal to react. Shared by `tryReact`
    /// (message-level reaction roll) — kept separate from `setReaction` (the actual wire
    /// call) so both `tryReact` and a future caller can pick without necessarily sending.
    let pickReactionEmoji (conf: BotConfiguration) (palette: string[]) (msg: Message) (from: User) : Task<string> =
        task {
            if conf.ReactionChoiceMode = OutcomeRouter.ModeRandom then
                return OutcomeRouter.pickRandomEmoji palette (Random.Shared.NextDouble())
            else
                let text = msg.Text |> Option.orElse msg.Caption |> Option.defaultValue ""
                let ctx: UsageContext = { ChatId = Some msg.Chat.Id; UserId = Some from.Id }
                match! chat.Complete(emojiPickRequest conf palette text, ctx, CancellationToken.None) with
                | Error err ->
                    logger.LogWarning("Reaction: emoji-pick LLM call failed: {Error} — falling back to a random pick", string err)
                    return OutcomeRouter.pickRandomEmoji palette (Random.Shared.NextDouble())
                | Ok resp ->
                    let picked = resp.Text.Trim()
                    if Array.contains picked palette then
                        return picked
                    else
                        return OutcomeRouter.pickRandomEmoji palette (Random.Shared.NextDouble())
        }

    /// Simple in-memory per-chat cooldown for the reaction channel (`REACTION_COOLDOWN_SECONDS`)
    /// — not persisted, same "a pod restart just resets it" tradeoff `chatLocks` already
    /// makes. Bounded the same way: only chats the bot actually listens to ever get an
    /// entry.
    let lastReactionAt = ConcurrentDictionary<int64, DateTimeOffset>()

    /// Atomically claims chat `chatId`'s reaction slot: true only if no reaction has been
    /// sent to this chat within REACTION_COOLDOWN_SECONDS (or ever) — and if so, the claim
    /// itself immediately stamps `now`, so two concurrent messages racing this check can't
    /// both slip through before the first claim becomes visible to the second.
    let tryClaimReactionSlot (conf: BotConfiguration) (chatId: int64) (now: DateTimeOffset) : bool =
        let cooldown = TimeSpan.FromSeconds(float conf.ReactionCooldownSeconds)
        let mutable claimed = false
        lastReactionAt.AddOrUpdate(
            chatId,
            (fun _ -> claimed <- true; now),
            (fun _ last -> if now - last >= cooldown then (claimed <- true; now) else last))
        |> ignore
        claimed

    /// The reaction channel (redesign, PR #253 follow-up): REACTION_PROBABILITY rolls on
    /// EVERY first-delivery message `handleTriggerableMessage` sees — triggered
    /// (addressed to the bot) or not — completely independent of whether that message
    /// also gets a text reply. REACTION_COOLDOWN_SECONDS (`tryClaimReactionSlot`) keeps a
    /// lively chat from being reacted to on every single message even at a high roll
    /// probability. A successful reaction is logged into `message_log` as a synthetic
    /// `[reaction] {emoji}` row — `message_id = -(original message_id)`, guaranteed free
    /// since real Telegram message ids are always positive — so later `/ask`-style recall
    /// and the LLM's own context window can see (and explain) why she reacted. Runs
    /// inside `withChatLock` (same convention as `tryInterject`/`tryMemeReact`) so it
    /// never races the main reply stream's own LLM call for the same chat. Best-effort: a
    /// rejected `SetMessageReaction` call is Warning-logged (inside `setReaction`) and
    /// simply skipped — this never blocks or retries the triggering message.
    let tryReact (conf: BotConfiguration) (msg: Message) (from: User) : Task<unit> =
        withChatLock msg.Chat.Id (fun () ->
            task {
                let countReact (kind: string) = Metrics.proactiveTotal.Add(1L, KeyValuePair("kind", box kind))
                if Random.Shared.NextDouble() >= conf.ReactionProbability then
                    return ()
                elif not (tryClaimReactionSlot conf msg.Chat.Id (time.GetUtcNow())) then
                    countReact "reaction_cooldown"
                else
                    let palette = reactionPalette conf
                    let! emoji = pickReactionEmoji conf palette msg from
                    let! ok = setReaction "Reaction" msg emoji
                    if ok then
                        do! logAndEmbed conf (
                                logRow msg.Chat.Id (-msg.MessageId) (botUserId conf) conf.BotUsername conf.BotUsername true
                                    (Some msg.MessageId) $"[reaction] {emoji}")
                            |> taskIgnore
                        countReact "reaction"
                    else
                        countReact "reaction_failed"
            })

    // ── Proactive behavior (Slice 8: interjections, meme reactions) ─────────
    //
    // Both hooks below fire from handleTriggerableMessage's "not triggered, first
    // delivery" branch, fire-and-forget (BotInfra.Utils.fireAndForget) — never on the
    // request path that just logged the message. Each takes withChatLock itself: since
    // fireAndForget starts the task inline but the outer withChatLock callback (still
    // executing synchronously up to that point) finishes and releases the semaphore
    // before this task's own WaitAsync ever completes, there's no deadlock — the
    // interjection/meme-react work simply queues behind whatever else touches this chat's
    // lock next, same serialization guarantee normal triggered messages get.

    // buildTranscript aliased above (CommandCores.buildTranscript) — shared with /summary.

    let interjectRequest (conf: BotConfiguration) (transcript: string) : ChatRequest =
        { Deployment = conf.LlmDeployment
          Messages =
            [ { Role = ChatRole.System
                Content = [ ContentPart.Text conf.InterjectPrompt ]
                ToolCalls = []
                ToolCallId = None }
              { Role = ChatRole.User
                Content = [ ContentPart.Text transcript ]
                ToolCalls = []
                ToolCallId = None } ]
          Tools = []
          Temperature = None
          MaxTokens = None
          ReasoningEffort = None }

    /// Willingness-gated interjection (plan §2): INTERJECT_PROBABILITY roll, then a burst
    /// check (>= BURST_MSGS messages from >= BURST_SPEAKERS distinct users in the last
    /// BURST_WINDOW_MINUTES, DbService.BurstStats), then a cooldown check (no bot message
    /// in this chat in the last INTERJECT_COOLDOWN_MINUTES, DbService.HasBotMessageSince)
    /// — cheapest check first, no DB round trip at all when the roll itself fails. Only
    /// once all three hold does the recent-context INTERJECT_PROMPT LLM call fire; a
    /// "PASS" (trim/case-insensitive) response stays silent, anything else goes out as a
    /// plain (non-reply) message and is logged like any other bot reply.
    let tryInterject (conf: BotConfiguration) (msg: Message) (from: User) : Task<unit> =
        withChatLock msg.Chat.Id (fun () ->
            task {
                if Random.Shared.NextDouble() >= conf.InterjectProbability then
                    return ()
                else
                    let burstSince = time.GetUtcNow().UtcDateTime.AddMinutes(-float conf.BurstWindowMinutes)
                    let! burst = db.BurstStats(msg.Chat.Id, burstSince)

                    if burst.message_count < int64 conf.BurstMsgs || burst.distinct_users < int64 conf.BurstSpeakers then
                        return ()
                    else
                        let cooldownSince = time.GetUtcNow().UtcDateTime.AddMinutes(-float conf.InterjectCooldownMinutes)
                        let! onCooldown = db.HasBotMessageSince(msg.Chat.Id, cooldownSince)

                        if onCooldown then
                            return ()
                        else
                            let! rows = db.RecentContext(msg.Chat.Id, conf.ContextWindowMessages)
                            let transcript = buildTranscript rows
                            let ctx: UsageContext = { ChatId = Some msg.Chat.Id; UserId = Some from.Id }

                            match! chat.Complete(interjectRequest conf transcript, ctx, CancellationToken.None) with
                            | Error err -> logger.LogWarning("Interject: LLM call failed: {Error}", string err)
                            | Ok resp when String.IsNullOrWhiteSpace resp.Text ->
                                logger.LogWarning("Interject: LLM returned empty text — staying silent")
                            | Ok resp when resp.Text.Trim().Equals("PASS", StringComparison.OrdinalIgnoreCase) ->
                                Metrics.proactiveTotal.Add(1L, KeyValuePair("kind", box "interject_pass"))
                            | Ok resp ->
                                let! sent = BotHelpers.sendMessage tg msg.Chat.Id resp.Text
                                do! logAndEmbed conf (
                                        logRow msg.Chat.Id sent.MessageId (botUserId conf) conf.BotUsername conf.BotUsername true
                                            None resp.Text)
                                    |> taskIgnore
                                Metrics.proactiveTotal.Add(1L, KeyValuePair("kind", box "interject"))
            })

    /// Parses the meme-react vision LLM's strict JSON contract (`MemeAction`, above) — a
    /// missing/non-string `action`, or non-object/malformed JSON overall, is `None`; the
    /// caller treats that the same as an explicit "pass" (plus a Warning log).
    let parseMemeJson (json: string) : MemeAction option =
        try
            use doc = JsonDocument.Parse(json: string)
            if doc.RootElement.ValueKind <> JsonValueKind.Object then
                None
            else
                match doc.RootElement.TryGetProperty "action" with
                | true, a when a.ValueKind = JsonValueKind.String ->
                    let stringOr (prop: string) =
                        match doc.RootElement.TryGetProperty prop with
                        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                        | _ -> ""
                    Some { Action = a.GetString(); Emoji = stringOr "emoji"; Text = stringOr "text" }
                | _ -> None
        with _ ->
            None

    /// Downloads `msg`'s largest photo and re-encodes it as a base64 data: URL — same
    /// shape as ResponderService's vision fetch, duplicated locally (not shared) since it's
    /// a two-line wrapper and ResponderService's version is private to that type.
    let tryFetchPhotoImagePart (conf: BotConfiguration) (msg: Message) : Task<ContentPart option> =
        task {
            match BotHelpers.largestPhoto msg with
            | None -> return None
            | Some photo ->
                try
                    let! file = tg.CallExn(Req.GetFile.Make(photo.FileId))
                    match file.FilePath with
                    | None -> return None
                    | Some fp when String.IsNullOrWhiteSpace fp -> return None
                    | Some fp ->
                        let! bytes = tg.DownloadFile fp
                        let url = $"data:image/jpeg;base64,{Convert.ToBase64String bytes}"
                        return Some(ContentPart.ImageUrl(url, Some conf.VisionDetail))
                with ex ->
                    logger.LogWarning(ex, "Meme react: failed to fetch photo {FileId} — skipping", photo.FileId)
                    return None
        }

    let memeReactRequest (conf: BotConfiguration) (palette: string[]) (imagePart: ContentPart) (caption: string) : ChatRequest =
        let captionLine = if caption = "" then "" else $"\n\nПодпись к фото: {caption}"
        // Same shared, hot-reloadable REACTION_PALETTE the S6 emoji outcome picks from
        // (BotService.reactionPalette) — grounds the model in the exact set it's allowed
        // to answer with, instead of relying solely on post-hoc validation below.
        let allowedText = String.concat "" palette
        let systemPrompt = conf.MemeReactPrompt + $"\n\nEmoji-реакция (\"emoji\") ДОЛЖНА быть ровно одним символом из этого набора: {allowedText}."
        { Deployment = conf.LlmDeployment
          Messages =
            [ { Role = ChatRole.System
                Content = [ ContentPart.Text systemPrompt ]
                ToolCalls = []
                ToolCallId = None }
              { Role = ChatRole.User
                Content = [ ContentPart.Text $"Оцени это фото.{captionLine}"; imagePart ]
                ToolCalls = []
                ToolCallId = None } ]
          Tools = []
          Temperature = None
          MaxTokens = None
          ReasoningEffort = None }

    /// Meme reaction (plan §3): MEME_REACT_PROBABILITY roll, then a vision LLM call
    /// (MEME_REACT_PROMPT) that must answer strict JSON {action,emoji,text}. "react" sets
    /// a reaction (Req.SetMessageReaction) from the same shared REACTION_PALETTE the S6
    /// emoji outcome picks from (BotService.reactionPalette) — an emoji outside that set is
    /// treated as a no-op (Warning-logged), never sent to Telegram unchecked. "comment"
    /// sends a one-liner reply; blank text is a no-op.
    /// "pass", an unrecognized action, a failed LLM call, or malformed JSON all count as
    /// meme_pass — the spec's "malformed JSON -> treat as pass, log Warning".
    let tryMemeReact (conf: BotConfiguration) (msg: Message) (from: User) : Task<unit> =
        withChatLock msg.Chat.Id (fun () ->
            task {
                if Random.Shared.NextDouble() >= conf.MemeReactProbability then
                    return ()
                else
                    match! tryFetchPhotoImagePart conf msg with
                    | None -> return ()
                    | Some imagePart ->
                        let palette = reactionPalette conf
                        let caption = msg.Caption |> Option.defaultValue ""
                        let ctx: UsageContext = { ChatId = Some msg.Chat.Id; UserId = Some from.Id }
                        let countMeme (kind: string) = Metrics.proactiveTotal.Add(1L, KeyValuePair("kind", box kind))

                        match! chat.Complete(memeReactRequest conf palette imagePart caption, ctx, CancellationToken.None) with
                        | Error err ->
                            logger.LogWarning("Meme react: LLM call failed: {Error}", string err)
                            countMeme "meme_pass"
                        | Ok resp ->
                            match parseMemeJson resp.Text with
                            | None ->
                                logger.LogWarning("Meme react: malformed JSON response — treating as pass: {Text}", resp.Text)
                                countMeme "meme_pass"
                            | Some m ->
                                match m.Action.Trim().ToLowerInvariant() with
                                | "react" when Array.contains m.Emoji palette ->
                                    let! ok = setReaction "Meme react" msg m.Emoji
                                    countMeme (if ok then "meme_react" else "meme_pass")
                                | "react" ->
                                    logger.LogWarning("Meme react: disallowed/empty emoji '{Emoji}' — skipping", m.Emoji)
                                    countMeme "meme_pass"
                                | "comment" when not (String.IsNullOrWhiteSpace m.Text) ->
                                    let! sent = BotHelpers.sendTextReply tg msg.Chat.Id m.Text msg.MessageId
                                    do! logAndEmbed conf (
                                            logRow msg.Chat.Id sent.MessageId (botUserId conf) conf.BotUsername conf.BotUsername true
                                                (Some msg.MessageId) m.Text)
                                        |> taskIgnore
                                    countMeme "meme_comment"
                                | "comment" ->
                                    logger.LogWarning("Meme react: comment action with empty text — skipping")
                                    countMeme "meme_pass"
                                | "pass" -> countMeme "meme_pass"
                                | other ->
                                    logger.LogWarning("Meme react: unknown action '{Action}' — treating as pass", other)
                                    countMeme "meme_pass"
            })

    /// Shared by plain text messages and photo messages: logs `logText` to message_log,
    /// fires the independent reaction-channel roll (`tryReact`, fire-and-forget — see its
    /// own doc comment) on every first-delivery message regardless of whether it's
    /// triggered, then checks `triggerText` (raw text/caption, matching whatever entity
    /// array the mention offsets are relative to) for a trigger: a TRIGGERED message
    /// ALWAYS dispatches to the responder now (the old OUTCOME_WEIGHTS silence/emoji roll
    /// for triggered messages is gone — redesign, PR #253 follow-up). When NOT triggered
    /// (first delivery only, never on a duplicate), fires `onNotTriggered` fire-and-forget
    /// — Slice 8's interjection/meme-react hooks — after logging, never delaying this
    /// response.
    let handleTriggerableMessage
        (conf: BotConfiguration)
        (msg: Message)
        (from: User)
        (logText: string)
        (triggerText: string)
        (onNotTriggered: (unit -> Task<unit>) option)
        =
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
            else
                // Independent reaction channel: rolls on this first-delivery message
                // whether or not it's triggered, fire-and-forget so it never delays the
                // reply path below (`tryReact` takes its own `withChatLock`, same
                // convention as `tryInterject`/`tryMemeReact`).
                fireAndForget logger "reaction.roll" (fun () -> tryReact conf msg from :> Task)

                if isTriggered conf triggerText msg then
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
                    match onNotTriggered with
                    | Some hook -> fireAndForget logger "proactive.hook" (fun () -> hook () :> Task)
                    | None -> ()
        }

    let handleMessage (conf: BotConfiguration) (msg: Message) (from: User) (text: string) =
        handleTriggerableMessage conf msg from text text (Some(fun () -> tryInterject conf msg from))

    /// Photo message flow: logs as `[photo] {caption}` (bare `[photo]` for no caption,
    /// mirroring S1's `[voice]` convention) and checks the RAW caption — not the logged
    /// text — against CaptionEntities for a mention, since entity offsets are relative to
    /// the caption Telegram sent, not our logging prefix. The image itself is fetched by
    /// ResponderService at respond time (from msg.Photo directly, via ITelegramApi), not
    /// stored here — message_log only ever holds text.
    let handlePhotoMessage (conf: BotConfiguration) (msg: Message) (from: User) =
        let caption = msg.Caption |> Option.defaultValue ""
        let logText = if caption = "" then "[photo]" else $"[photo] {caption}"
        handleTriggerableMessage conf msg from logText caption (Some(fun () -> tryMemeReact conf msg from))

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
    /// image for a `/img` reply-to-a-photo prompt. Moved to BotHelpers (S10 PR1
    /// prerequisite) so ResponderService can share it for the NL `generate_image` tool's
    /// ToolExecContext.SourceImage; rebound locally so the one call site below is unchanged.
    let tryFetchReplySourceImage = BotHelpers.tryFetchReplySourceImage tg logger

    /// `/img` / `!img` command flow (plan §B2). Always logs the command as
    /// `[img-cmd] {prompt}` first, then branches: empty prompt -> RU usage hint;
    /// IMAGE_GEN_ENABLED=false -> RU "disabled" reply; otherwise sends a "рисую..."
    /// placeholder, resolves an optional img2img source image from the reply target, and
    /// delegates the generate+caption+send core to MediaActions.generateImage (S10 PR1 —
    /// shared with the NL `generate_image` tool path) — branching on the returned
    /// MediaOutcome for placeholder cleanup, message_log bookkeeping, and metrics. Never
    /// dispatches to ResponderService — a command message is fully handled here, regardless
    /// of whether it also happens to contain the bot's name/mention.
    /// `alitabot_command_total` is incremented centrally by the dispatcher
    /// (see `commands`/OnUpdate), not here.
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

                match!
                    MediaActions.generateImage
                        logger imageGen chat tg conf msg.Chat.Id msg.MessageId sourceImage prompt usageCtx
                with
                | MediaOutcome.Sent(sentPhoto, caption) ->
                    // OQ3 (accepted): message_log logs the real caption now, not the raw
                    // (truncated) prompt — matches /say's "[voice] {text}" convention.
                    do! logAndEmbed conf (
                            logRow msg.Chat.Id sentPhoto.MessageId (botUserId conf) conf.BotUsername conf.BotUsername true
                                (Some msg.MessageId) $"[image] {caption}")
                        |> taskIgnore
                    do! BotHelpers.deleteMessage tg msg.Chat.Id placeholder.MessageId
                    let outcome = if sourceImage.IsSome then "image_edited" else "image_generated"
                    %a.SetTag("outcome", outcome)
                    countOutcome outcome
                | MediaOutcome.GenFailed reason ->
                    logger.LogWarning("Image generation failed: {Reason}", reason)
                    do! BotHelpers.editMessageText tg msg.Chat.Id placeholder.MessageId reason
                    let outcome =
                        if reason.Contains("перегружена", StringComparison.Ordinal) then
                            "image_failed_transient"
                        else
                            "image_failed"
                    %a.SetTag("outcome", outcome)
                    countOutcome outcome
                | MediaOutcome.Refused reason ->
                    // Defensive only: the empty-prompt/IMAGE_GEN_ENABLED guards above already
                    // rule these cases out before the placeholder is ever sent, so this
                    // should never actually fire from the command path.
                    do! BotHelpers.editMessageText tg msg.Chat.Id placeholder.MessageId reason
                    %a.SetTag("outcome", "image_refused")
                    countOutcome "image_refused"
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
    /// bot's own reply row. /summary doesn't use this: it has more outcome branches (empty
    /// history, empty LLM response, LLM failure), not because of any ephemeral send — its
    /// reply is a plain sendTextReply like every other command (see handleSummaryCommand).
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

    /// Lenient parse of the LLM_MODELS bot_setting (JSON_BLOB array of
    /// `{"model": "...", "deployment": "..."}`). Malformed JSON, a non-array value, or an
    /// entry missing either field (or with an empty one) is skipped — /model's switchable
    /// list is whatever parses cleanly, same lenient posture the old MODEL_ALLOWLIST parse
    /// had.
    /// `/model` — core logic (parse LLM_MODELS, show or switch) moved to
    /// `CommandCores.modelCore` (S10 PR2) — shared with the `switch_model` NL tool, which
    /// is NOT admin-gated either, mirroring this command.
    let handleModelCommand (conf: BotConfiguration) (msg: Message) (from: User) (args: string) =
        handleSimpleCommand "model" conf msg from args (fun () -> modelCore conf args)

    /// `/usage` — core logic moved to `CommandCores.usageCore` (S10 PR2) — shared with the
    /// `show_usage` NL tool, which is NOT admin-gated either, mirroring this command.
    let handleUsageCommand (conf: BotConfiguration) (msg: Message) (from: User) (args: string) =
        handleSimpleCommand "usage" conf msg from args (fun () -> usageCore conf)

    /// `/summary [count]` — core logic moved to `CommandCores.summaryCore` (S10 PR2) —
    /// shared with the `summarize_chat` NL tool. (Previously sent via Bot API 10.2's
    /// ephemeral `sendMessage`, visible only to the requester — retired after staging
    /// feedback found those replies invisible in practice; see the "Ephemeral message
    /// probe [RETIRED]" section of src/AlitaBot/README.md and docs/TECH-DEBT.md for the
    /// empirical writeup.)
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
                    let! sent = BotHelpers.sendTextReply tg msg.Chat.Id text msg.MessageId
                    do! logAndEmbed conf (
                            logRow msg.Chat.Id sent.MessageId (botUserId conf) conf.BotUsername
                                conf.BotUsername true (Some msg.MessageId) text)
                        |> taskIgnore
                    %a.SetTag("outcome", outcome)
                    countOutcome outcome
                }

            if not inserted then
                %a.SetTag("outcome", "duplicate_update")
                countOutcome "duplicate_update"
            else
                let usageCtx: UsageContext = { ChatId = Some msg.Chat.Id; UserId = Some from.Id }
                let! text, outcome = summaryCore conf msg.Chat.Id usageCtx args
                do! reply text outcome
        }

    // ── /ask (Slice 5a: semantic search over this chat's message_embedding) ────
    //
    // Core logic (embed question -> SemanticSearch -> grounded LLM answer) moved to
    // `CommandCores.askCore` (S10 PR2) — shared with the `ask_chat_history` NL tool.

    /// `/ask <question>` — see `CommandCores.askCore`'s doc comment for the guardrails
    /// (empty question, embed failure, no matches above the similarity floor).
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
            else
                let usageCtx: UsageContext = { ChatId = Some msg.Chat.Id; UserId = Some from.Id }
                let! text, outcome = askCore conf msg.Chat.Id usageCtx question
                do! reply text outcome
        }

    // ── /dossier, /forget-me (Slice 5b: per-person dossiers) ───────────────────
    //
    // /dossier's core (resolve target -> render summary+facts, or CommandCores.NoDossierText)
    // moved to `CommandCores.dossierCore` (S10 PR2) — shared with the `show_dossier` NL tool.

    /// `/dossier` (self, no arg) or `/dossier @username` (another chat member, `@`
    /// optional) — see `CommandCores.dossierCore`'s doc comment.
    let handleDossierCommand (conf: BotConfiguration) (msg: Message) (from: User) (args: string) =
        handleSimpleCommand "dossier" conf msg from args (fun () -> dossierCore from.Id args)

    /// `/forget-me` — opts the requester out of memory (memory_opt_out), hard-deletes
    /// their interaction_memory/person_dossier/message_embedding rows (DbService.
    /// PurgeUserMemory), and confirms. message_log itself is untouched — it's the shared
    /// chat record, not personal memory (see the V4 migration). From this point on the
    /// requester is excluded from the nightly dossier job, the inline embedding pipeline,
    /// and recall injection (ResponderService) — see DossierService/MessageLog.tryEmbed.
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
    // sendTextReply like /usage, /dossier, /forget-me. All four cores (target resolution,
    // ammo gathering, the JSON-contract LLM calls, rendering) moved to CommandCores.fs
    // (S10 PR2) — shared with roast_user/show_awards/show_quote/show_karma NL tools.

    /// `/roast [@username | reply-to-target]` — see `CommandCores.roastCommandCore`'s doc
    /// comment for target resolution + ammunition gathering. Delivered via
    /// `Mdv2Delivery.sendFinal` (non-stream LLM call -> MDV2 render -> reply).
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
                let usageCtx: UsageContext = { ChatId = Some msg.Chat.Id; UserId = Some from.Id }
                let! text, outcome = roastCommandCore conf msg from usageCtx args
                do! reply text outcome
        }

    /// `/awards` — see `CommandCores.awardsCore`'s doc comment.
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
                let usageCtx: UsageContext = { ChatId = Some msg.Chat.Id; UserId = Some from.Id }
                let! text, outcome = awardsCore conf msg.Chat.Id usageCtx
                do! reply text outcome
        }

    /// `/quote` — see `CommandCores.quoteCore`'s doc comment.
    let handleQuoteCommand (conf: BotConfiguration) (msg: Message) (from: User) (args: string) =
        handleSimpleCommand "quote" conf msg from args (fun () ->
            let usageCtx: UsageContext = { ChatId = Some msg.Chat.Id; UserId = Some from.Id }
            quoteCore conf msg.Chat.Id usageCtx)

    /// `/karma [@user]` — see `CommandCores.karmaCore`'s doc comment.
    let handleKarmaCommand (conf: BotConfiguration) (msg: Message) (from: User) (args: string) =
        handleSimpleCommand "karma" conf msg from args (fun () -> karmaCore from.Id args)

    // ── /say (Slice 9 stretch: TTS voice replies) ───────────────────────────────
    //
    // The generate+synthesize+send core moved to MediaActions.speakText (S10 PR2, mirroring
    // PR1's MediaActions.generateImage refactor) — shared with the `speak_text` NL tool.
    // parseSayArgs/validTtsVoices stay here: pure command-syntax parsing (the ambiguous
    // "is the first token a voice name or the message itself" sniffing), not needed by the
    // tool (which gets an explicit `voice` argument from the model).

    /// The OpenAI/Azure TTS voice roster (gpt-4o-mini-tts family) — `/say`'s only valid
    /// explicit voice names, matched case-insensitively.
    let validTtsVoices =
        [ "alloy"; "ash"; "ballad"; "coral"; "echo"; "fable"; "nova"; "onyx"; "sage"; "shimmer"; "verse" ]

    let validTtsVoicesText = String.concat ", " validTtsVoices

    /// Splits `/say`'s args into (voice selector, explicit spoken text). The first
    /// whitespace-separated token is treated as an explicit voice selector in two cases:
    /// (1) it's followed by more text ("/say nova привет" -> Valid "nova", Some "привет"),
    /// or (2) it's the ONLY token AND the command replies to another message (that
    /// message's text supplies the spoken content — "/say nova" replying to "привет" ->
    /// Valid "nova", None). A first token that matches no known voice is left as ordinary
    /// text in case (1) ("/say мама мыла раму" is just spoken with the default voice, no
    /// error) but reported as SayVoiceArg.Invalid in case (2), where a lone leftover word
    /// has no other plausible reading once the reply already supplies the message. Outside
    /// a reply, a lone single token — matching a voice or not — is ambiguous between "just
    /// the message" and "voice with no text", so it's always read as plain text.
    let parseSayArgs (hasReplyText: bool) (args: string) : SayVoiceArg * string option =
        let trimmed = args.Trim()
        if trimmed = "" then
            SayVoiceArg.NoVoice, None
        else
            let spaceIdx = trimmed.IndexOfAny([| ' '; '\t'; '\n' |])
            if spaceIdx < 0 then
                let lower = trimmed.ToLowerInvariant()
                if List.contains lower validTtsVoices then
                    if hasReplyText then SayVoiceArg.Valid lower, None else SayVoiceArg.NoVoice, Some trimmed
                elif hasReplyText then
                    SayVoiceArg.Invalid lower, None
                else
                    SayVoiceArg.NoVoice, Some trimmed
            else
                let first = trimmed.Substring(0, spaceIdx)
                let rest = trimmed.Substring(spaceIdx + 1).Trim()
                let lower = first.ToLowerInvariant()
                if List.contains lower validTtsVoices then
                    SayVoiceArg.Valid lower, Some rest
                else
                    SayVoiceArg.NoVoice, Some trimmed

    /// `/say [voice] <text>` (or, replying to a message with no text of its own, voices
    /// ITS text) — delegates the middle (synthesize -> re-encode -> send) to
    /// `MediaActions.speakText`. Command-only guards (invalid voice name, empty text, over
    /// SAY_MAX_CHARS) run BEFORE calling it, same "avoid a doomed call" pattern
    /// `handleImageCommand` uses for IMAGE_GEN_ENABLED — `speakText`'s own internal guards
    /// are defensive-only here, and the ones that actually fire for the `speak_text` tool.
    let handleSayCommand (conf: BotConfiguration) (msg: Message) (from: User) (args: string) =
        task {
            use a = botActivity.StartActivity("handleSayCommand")
            %a.SetTag("chatId", msg.Chat.Id)
            %a.SetTag("fromId", from.Id)

            let! inserted =
                logAndEmbed conf (
                    logRow msg.Chat.Id msg.MessageId from.Id (Option.toObj from.Username) (displayNameOf from) from.IsBot
                        (match msg.ReplyToMessage with Some r -> Some r.MessageId | None -> None)
                        (if args = "" then "[say-cmd]" else $"[say-cmd] {args}"))

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
            else
                let replyText = msg.ReplyToMessage |> Option.bind (fun r -> r.Text |> Option.orElse r.Caption)
                let voiceArg, textArg = parseSayArgs replyText.IsSome args
                let text = (textArg |> Option.orElse replyText |> Option.defaultValue "").Trim()

                match voiceArg with
                | SayVoiceArg.Invalid bad ->
                    do! reply $"Не знаю такой голос: «{bad}». Доступные: {validTtsVoicesText}." "say_invalid_voice"
                | _ when String.IsNullOrWhiteSpace text ->
                    do!
                        reply
                            "Скажи, что озвучить: `/say привет`. Или ответь этой командой на сообщение — озвучу его текст."
                            "say_empty_text"
                | _ when text.Length > conf.SayMaxChars ->
                    do! reply $"Слишком длинный текст — максимум {conf.SayMaxChars} символов." "say_too_long"
                | _ ->
                    let voice = match voiceArg with SayVoiceArg.Valid v -> Some v | _ -> None
                    let usageCtx: UsageContext = { ChatId = Some msg.Chat.Id; UserId = Some from.Id }

                    match! MediaActions.speakText logger speech tg conf msg.Chat.Id msg.MessageId voice text usageCtx with
                    | MediaOutcome.Sent(sent, spokenText) ->
                        do! logAndEmbed conf (
                                logRow msg.Chat.Id sent.MessageId (botUserId conf) conf.BotUsername conf.BotUsername true
                                    (Some msg.MessageId) $"[voice] {spokenText}")
                            |> taskIgnore
                        %a.SetTag("outcome", "say_delivered")
                        countOutcome "say_delivered"
                    | MediaOutcome.GenFailed reason -> do! reply reason "say_failed"
                    | MediaOutcome.Refused reason ->
                        // Defensive only — the guards above already rule these cases out
                        // before speakText is ever called from the command path.
                        do! reply reason "say_refused"
        }

    // ── /song (Gemini/Lyria music generation) ───────────────────────────────────
    //
    // The generate+caption+send core moved to MediaActions.generateSong (S10 PR2, mirroring
    // PR1's MediaActions.generateImage refactor) — shared with the `generate_song` NL tool.
    // parseSongArgs stays here: pure command-syntax parsing (the `(style)` inline-flag
    // convention), not needed by the tool (which gets an explicit `style` argument).

    /// Splits `/song`'s args into an optional leading `(style hint)` and the rest (lyrics /
    /// description) — Matie-style inline flags, e.g. `(рок-баллада) текст песни...` ->
    /// `Some "рок-баллада", "текст песни..."`. A `(` with no matching `)` is treated as
    /// ordinary text (no style extracted) rather than an error — `/song (незакрытая скобка`
    /// still generates something instead of refusing on a punctuation slip.
    let parseSongArgs (args: string) : string option * string =
        let trimmed = args.Trim()
        if trimmed.StartsWith "(" then
            match trimmed.IndexOf ')' with
            | -1 -> None, trimmed
            | i ->
                let style = trimmed.Substring(1, i - 1).Trim()
                let rest = trimmed.Substring(i + 1).Trim()
                (if style = "" then None else Some style), rest
        else
            None, trimmed

    /// `/song [(style)] <lyrics or description>` — Gemini's Lyria music generation,
    /// delegating the middle (generate -> composeCaption -> re-encode -> send) to
    /// `MediaActions.generateSong`. Logs `[song-cmd] {args}` first (webhook-redelivery
    /// dedup guard, same convention as every other command), then: empty prompt -> RU usage
    /// hint; over `SONG_MAX_CHARS` -> RU refusal; on cooldown (`SONG_COOLDOWN_SECONDS`,
    /// per-invoker) -> RU cooldown reply, all checked BEFORE sending a "сочиняю..."
    /// placeholder (same "avoid a doomed call" pattern `handleImageCommand` uses) — the
    /// message_log reply row is now `[song] {caption}` (S10 PR2 fix: no more prompt-echo
    /// titles/log text — matches the `/img` OQ3 convention).
    let handleSongCommand (conf: BotConfiguration) (msg: Message) (from: User) (args: string) =
        task {
            use a = botActivity.StartActivity("handleSongCommand")
            %a.SetTag("chatId", msg.Chat.Id)
            %a.SetTag("fromId", from.Id)

            let! inserted =
                logAndEmbed conf (
                    logRow msg.Chat.Id msg.MessageId from.Id (Option.toObj from.Username) (displayNameOf from) from.IsBot
                        (match msg.ReplyToMessage with Some r -> Some r.MessageId | None -> None)
                        (if args = "" then "[song-cmd]" else $"[song-cmd] {args}"))

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
            else
                let styleHint, lyricsOrDesc = parseSongArgs args
                if String.IsNullOrWhiteSpace lyricsOrDesc then
                    do!
                        reply
                            "Напиши, что сочинить: `/song текст песни` или `/song (стиль) текст песни`, например `/song (рок-баллада) про баги в проде`."
                            "song_empty_prompt"
                elif lyricsOrDesc.Length > conf.SongMaxChars then
                    do! reply $"Слишком длинный текст — максимум {conf.SongMaxChars} символов." "song_too_long"
                else
                    let now = time.GetUtcNow().UtcDateTime
                    let! lastSong = db.LastSongAt(from.Id)
                    match lastSong with
                    | Some last when (now - last).TotalSeconds < float conf.SongCooldownSeconds ->
                        do! reply "рано, дай отдышаться — попробуй чуть позже" "song_cooldown"
                    | _ ->
                        let! placeholder = BotHelpers.sendTextReply tg msg.Chat.Id "сочиняю..." msg.MessageId
                        let usageCtx: UsageContext = { ChatId = Some msg.Chat.Id; UserId = Some from.Id }

                        match!
                            MediaActions.generateSong
                                logger musicGen chat tg db time conf msg.Chat.Id msg.MessageId from.Id styleHint lyricsOrDesc usageCtx
                        with
                        | MediaOutcome.Sent(sent, caption) ->
                            do! logAndEmbed conf (
                                    logRow msg.Chat.Id sent.MessageId (botUserId conf) conf.BotUsername conf.BotUsername true
                                        (Some msg.MessageId) $"[song] {caption}")
                                |> taskIgnore
                            do! BotHelpers.deleteMessage tg msg.Chat.Id placeholder.MessageId
                            %a.SetTag("outcome", "song_delivered")
                            countOutcome "song_delivered"
                        | MediaOutcome.GenFailed reason ->
                            logger.LogWarning("Music generation failed: {Reason}", reason)
                            do! BotHelpers.editMessageText tg msg.Chat.Id placeholder.MessageId reason
                            let outcome =
                                if reason.Contains("перегружена", StringComparison.Ordinal) then
                                    "song_failed_transient"
                                else
                                    "song_failed"
                            %a.SetTag("outcome", outcome)
                            countOutcome outcome
                        | MediaOutcome.Refused reason ->
                            // Defensive only — the guards above already rule these cases out
                            // before generateSong is ever called from the command path.
                            do! BotHelpers.editMessageText tg msg.Chat.Id placeholder.MessageId reason
                            %a.SetTag("outcome", "song_refused")
                            countOutcome "song_refused"
        }

    // ── /sql (Slice 9 stretch: admin-gated natural-language SQL analytics) ─────────────
    //
    // ADMIN_USER_IDS parsing + isAdmin moved to Services/Admin.fs (S10 PR1 prerequisite) so
    // ToolRegistry/ResponderService can gate AdminOnly NL tools (sql_query) the same way.
    // The rest of the core (LLM call -> SqlGuard -> execute -> render) moved to
    // `CommandCores.sqlCore` (S10 PR2) — shared with the `sql_query` NL tool.

    /// `/sql <question>` — ADMIN-GATED (ADMIN_USER_IDS): a non-admin gets a flat refusal,
    /// no LLM call at all. See `CommandCores.sqlCore`'s doc comment for the rest.
    let handleSqlCommand (conf: BotConfiguration) (msg: Message) (from: User) (question: string) =
        task {
            use a = botActivity.StartActivity("handleSqlCommand")
            %a.SetTag("chatId", msg.Chat.Id)
            %a.SetTag("fromId", from.Id)

            let! inserted =
                logAndEmbed conf (
                    logRow msg.Chat.Id msg.MessageId from.Id (Option.toObj from.Username) (displayNameOf from) from.IsBot
                        (match msg.ReplyToMessage with Some r -> Some r.MessageId | None -> None)
                        (if question = "" then "[sql-cmd]" else $"[sql-cmd] {question}"))

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
                let usageCtx: UsageContext = { ChatId = Some msg.Chat.Id; UserId = Some from.Id }
                let! text, outcome = sqlCore conf (Admin.isAdmin conf from.Id) usageCtx question
                do! reply text outcome
        }

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
              $"итоги последних N сообщений чата (по умолчанию {CommandCores.SummaryDefaultCount}, максимум {CommandCores.SummaryMaxCount}): /summary [N]"
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
            Handler = handleKarmaCommand }
          { Name = "say"
            Aliases = []
            Description = "озвучить текст голосом: /say [голос] текст, или ответом на сообщение"
            Handler = handleSayCommand }
          { Name = "song"
            Aliases = []
            Description = "сгенерировать музыку: /song [(стиль)] текст песни или описание"
            Handler = handleSongCommand }
          { Name = "sql"
            Aliases = []
            Description = "аналитика по базе на естественном языке, только для админов: /sql <вопрос>"
            Handler = handleSqlCommand } ]

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
