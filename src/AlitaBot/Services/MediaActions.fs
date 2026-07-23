namespace AlitaBot.Services

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Funogram.Telegram.Types
open AlitaBot
open AlitaBot.Llm
open BotInfra

/// Outcome of a media-generation action (S10 PR1: `generateImage`; PR2 adds
/// `generateSong`/`speakText` alongside it) — shared by both the `/img` command path and
/// the NL `generate_image` tool path, so the caller (BotService's command handler, or
/// ToolExecutorService) can branch on exactly one shape for placeholder cleanup/logging.
[<RequireQualifiedAccess>]
type MediaOutcome =
    /// The generated media was sent to Telegram; `caption` is the persona reaction text
    /// (MediaActions.composeCaption's output — NEVER the raw prompt, see its doc comment).
    | Sent of sent: Message * caption: string
    /// A guardrail refused the request before any generation call was made (feature
    /// disabled, empty prompt, cooldown) — `reason` is a fixed RU reply.
    | Refused of reason: string
    /// The underlying provider call failed — `reason` is a RU reply already split on
    /// transient-vs-generic (LlmError.isTransient), ready to show the user as-is.
    | GenFailed of reason: string

/// Shared generate+caption+send actions (S10 PR1 prerequisite refactor) — the "biggest
/// refactor in the plan": a single implementation of `/img`'s middle (guardrails ->
/// provider call -> caption -> send) reachable from BOTH the command path
/// (BotService.handleImageCommand) and the NL tool path (ToolExecutorService), so the
/// caption-composition fix (§ below) benefits both without duplicating the logic twice.
module MediaActions =

    /// Cheap non-stream LLM call in Alita's own persona (conf.SystemPrompt +
    /// conf.MediaCaptionPrompt) that reacts to having just generated `kind` (e.g.
    /// "изображение") for `prompt` — NEVER describes or repeats the prompt verbatim, no
    /// meta-commentary (that's TOOL_USE_PROMPT/MEDIA_CAPTION_PROMPT's job to enforce on the
    /// model side). Best-effort: an LLM failure or empty response falls back to a fixed
    /// short RU line ("Готово.") — a caption must never block media delivery.
    ///
    /// PROD INCIDENT (S10 first live use, 2026-07-22 staging): this call's `MaxTokens` was
    /// 60 — fine for a short 1-2 sentence caption in isolation, but gpt-5-mini is a
    /// reasoning model and `max_completion_tokens` covers BOTH its internal reasoning
    /// tokens AND the visible answer. `conf.SystemPrompt` is the FULL persona prompt (many
    /// paragraphs of behavioral rules, grown considerably by the persona-consistency pass),
    /// so the model reasons FAR more before answering than the plain system prompts every
    /// other cheap call in this codebase uses (tldrRequest/emojiPickRequest) — with no
    /// `reasoning_effort` cap, the reasoning tokens alone can exhaust the budget, leaving
    /// ZERO visible tokens: a 200 OK response with `finish_reason="length"` and empty
    /// `content`. That is NOT an error (`Error` branch) — it's a "successful" empty
    /// response, which the old code silently mapped to the "Готово." fallback with no log
    /// line anywhere, making the failure mode invisible (confirmed against prod Loki: no
    /// Warning/Error around the caption call for that window, only the empty fallback
    /// caption reaching the user). Bumping `MaxTokens` to 500 alone was NOT enough —
    /// re-verified against a REAL Azure call (not the fake suite): still 500/500 completion
    /// tokens spent on reasoning, zero visible text, `finish_reason="length"`. The actual
    /// fix is `ReasoningEffort = Some "minimal"`: this is a formulaic "react in 1-2
    /// sentences" ask that doesn't need deep reasoning, and "minimal" is the documented
    /// gpt-5 lever for bounding how much a reasoning model thinks before answering — kept
    /// together with the widened MaxTokens as a belt-and-braces safety margin, plus (b)
    /// logging the empty-response case (regardless of cause) so this doesn't go dark again.
    let composeCaption
        (logger: ILogger)
        (chat: IChatCompletion)
        (conf: BotConfiguration)
        (ctx: UsageContext)
        (kind: string)
        (prompt: string)
        : Task<string> =
        task {
            let request: ChatRequest =
                { Deployment = conf.LlmDeployment
                  Messages =
                    [ { Role = ChatRole.System
                        Content = [ ContentPart.Text(conf.SystemPrompt + "\n\n" + conf.MediaCaptionPrompt) ]
                        ToolCalls = []
                        ToolCallId = None }
                      { Role = ChatRole.User
                        Content = [ ContentPart.Text $"[{kind}]: {prompt}" ]
                        ToolCalls = []
                        ToolCallId = None } ]
                  Tools = []
                  Temperature = None
                  MaxTokens = Some 500
                  ReasoningEffort = Some "minimal" }

            match! chat.Complete(request, ctx, CancellationToken.None) with
            | Ok resp when not (String.IsNullOrWhiteSpace resp.Text) -> return resp.Text.Trim()
            | Ok resp ->
                logger.LogWarning(
                    "MediaActions.composeCaption: LLM call succeeded but returned empty/whitespace text for a {Kind} caption (finish_reason={FinishReason}, completion_tokens={CompletionTokens}) — falling back to the fixed caption. Likely gpt-5-mini reasoning tokens exhausting MaxTokens before any visible text.",
                    kind,
                    resp.FinishReason,
                    (resp.Usage |> Option.map (fun u -> u.CompletionTokens) |> Option.defaultValue -1))
                return "Готово."
            | Error _ ->
                // The provider layer already Warning/Error-logs the failure itself
                // (AzureWire.logLlmError / AzureFoundryChat.Complete) — no need to duplicate.
                return "Готово."
        }

    /// Core of `/img` (command AND `generate_image` tool): guardrails (IMAGE_GEN_ENABLED,
    /// empty prompt) -> `imageGen.Generate` -> `composeCaption` -> `sendPhotoReply`. The
    /// caller supplies the placeholder lifecycle itself (the command path wants a
    /// "рисую..." placeholder message; the NL tool path doesn't send one) — this function
    /// never touches a placeholder.
    let generateImage
        (logger: ILogger)
        (imageGen: IImageGen)
        (chat: IChatCompletion)
        (tg: ITelegramApi)
        (conf: BotConfiguration)
        (chatId: int64)
        (replyToMessageId: int64)
        (sourceImage: byte[] option)
        (prompt: string)
        (ctx: UsageContext)
        : Task<MediaOutcome> =
        task {
            if String.IsNullOrWhiteSpace prompt then
                return MediaOutcome.Refused "Напиши, что нарисовать."
            elif not conf.ImageGenEnabled then
                return MediaOutcome.Refused "Генерация картинок сейчас выключена."
            else
                match! imageGen.Generate(prompt, sourceImage, ctx, CancellationToken.None) with
                | Ok(bytes, _usage) ->
                    let! caption = composeCaption logger chat conf ctx "изображение" prompt
                    let! sent = BotHelpers.sendPhotoReply tg chatId bytes caption replyToMessageId
                    return MediaOutcome.Sent(sent, caption)
                | Error err ->
                    // Transient (rate-limited / Gemini 503 "high demand") gets a distinct RU
                    // reply that tells the user retrying might actually help, instead of the
                    // generic shrug — see LlmTypes.LlmError.isTransient's doc comment.
                    let reason =
                        if LlmError.isTransient err then
                            "Модель перегружена, попробуй ещё раз через минутку 🙏"
                        else
                            "Не получилось нарисовать 🙁"
                    return MediaOutcome.GenFailed reason
        }

    /// Truncation length for `/song`'s delivered audio title (Bot API `sendAudio`
    /// `title` field) — now built from `composeCaption`'s output (S10 PR2 fix: no more
    /// prompt-echo titles), so this constant lives here next to where the title is
    /// actually built rather than in BotService.
    [<Literal>]
    let SongTitleMaxLen = 60

    /// True when `bytes` starts with the Ogg container magic ("OggS") — Azure's
    /// audio/speech `response_format=opus` normally already yields one.
    let isOggContainer (bytes: byte[]) =
        bytes.Length >= 4 && bytes[0] = 0x4Fuy && bytes[1] = 0x67uy && bytes[2] = 0x67uy && bytes[3] = 0x53uy

    /// Best-effort re-encode of non-Ogg TTS output into ogg/opus via `ffmpeg`, if it's on
    /// PATH. Returns None when ffmpeg is missing or the conversion itself fails — the
    /// caller falls back to sending the raw bytes as a regular audio attachment instead of
    /// a voice note in that case.
    let tryConvertToOggOpus (logger: ILogger) (bytes: byte[]) : Task<byte[] option> =
        task {
            let inPath = Path.GetTempFileName()
            let outPath = Path.ChangeExtension(Path.GetTempFileName(), ".ogg")
            try
                try
                    do! File.WriteAllBytesAsync(inPath, bytes)
                    let psi =
                        ProcessStartInfo(
                            FileName = "ffmpeg",
                            Arguments = $"-y -i \"{inPath}\" -c:a libopus -b:a 32k \"{outPath}\"",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true)
                    use proc = new Process(StartInfo = psi)
                    if not (proc.Start()) then
                        return None
                    else
                        let! _stdout = proc.StandardOutput.ReadToEndAsync()
                        let! _stderr = proc.StandardError.ReadToEndAsync()
                        do! proc.WaitForExitAsync()
                        if proc.ExitCode = 0 && File.Exists outPath then
                            let! converted = File.ReadAllBytesAsync(outPath)
                            return Some converted
                        else
                            return None
                with ex ->
                    logger.LogWarning(ex, "/say: ffmpeg conversion to ogg/opus failed — falling back to sendAudio")
                    return None
            finally
                (try File.Delete inPath with _ -> ())
                (try File.Delete outPath with _ -> ())
        }

    /// Best-effort re-encode of Lyria's audio bytes into mp3 via `ffmpeg`, mirroring
    /// `tryConvertToOggOpus`'s pattern: mp3 (not ogg/opus) because `/song` delivers via
    /// `sendAudio` with a `title` — a regular audio-player attachment, not a voice-note
    /// bubble. Returns `None` when `ffmpeg` is missing or the conversion itself fails.
    let tryConvertToMp3 (logger: ILogger) (bytes: byte[]) : Task<byte[] option> =
        task {
            let inPath = Path.GetTempFileName()
            let outPath = Path.ChangeExtension(Path.GetTempFileName(), ".mp3")
            try
                try
                    do! File.WriteAllBytesAsync(inPath, bytes)
                    let psi =
                        ProcessStartInfo(
                            FileName = "ffmpeg",
                            Arguments = $"-y -i \"{inPath}\" -c:a libmp3lame -b:a 128k \"{outPath}\"",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true)
                    use proc = new Process(StartInfo = psi)
                    if not (proc.Start()) then
                        return None
                    else
                        let! _stdout = proc.StandardOutput.ReadToEndAsync()
                        let! _stderr = proc.StandardError.ReadToEndAsync()
                        do! proc.WaitForExitAsync()
                        if proc.ExitCode = 0 && File.Exists outPath then
                            let! converted = File.ReadAllBytesAsync(outPath)
                            return Some converted
                        else
                            return None
                with ex ->
                    logger.LogWarning(ex, "/song: ffmpeg conversion to mp3 failed — sending raw bytes as-is")
                    return None
            finally
                (try File.Delete inPath with _ -> ())
                (try File.Delete outPath with _ -> ())
        }

    /// Core of `/song` (command AND `generate_song` tool, S10 PR2): guardrails
    /// (SONG_MAX_CHARS, SONG_COOLDOWN_SECONDS via `db.LastSongAt`/`db.RecordSong` — the SAME
    /// DbService calls the `/song` command uses) -> `musicGen.Generate` -> `composeCaption`
    /// (kind="песню") -> re-encode to mp3 (best-effort) -> `sendAudioReplyWithTitle`, title
    /// built from the COMPOSED caption (S10 PR2 fix: no more prompt-echo titles — the old
    /// `/song` title was `lyricsOrDesc` truncated verbatim). The cooldown is only stamped
    /// (`db.RecordSong`) on an actual successful delivery, mirroring `/roast`'s
    /// `RecordRoast` convention. The command path ALSO pre-checks length/cooldown itself
    /// (same "defensive, avoid showing a placeholder for a doomed request" pattern
    /// `generateImage`'s callers use for IMAGE_GEN_ENABLED) — these guards are the ones
    /// that actually fire for the `generate_song` tool path, which has no placeholder and
    /// never pre-checks.
    let generateSong
        (logger: ILogger)
        (musicGen: IMusicGen)
        (chat: IChatCompletion)
        (tg: ITelegramApi)
        (db: DbService)
        (time: TimeProvider)
        (conf: BotConfiguration)
        (chatId: int64)
        (replyToMessageId: int64)
        (userId: int64)
        (styleHint: string option)
        (lyricsOrDesc: string)
        (ctx: UsageContext)
        : Task<MediaOutcome> =
        task {
            if String.IsNullOrWhiteSpace lyricsOrDesc then
                return MediaOutcome.Refused "Напиши, что сочинить: `/song текст песни` или `/song (стиль) текст песни`."
            elif lyricsOrDesc.Length > conf.SongMaxChars then
                return MediaOutcome.Refused $"Слишком длинный текст — максимум {conf.SongMaxChars} символов."
            else
                let now = time.GetUtcNow().UtcDateTime
                let! lastSong = db.LastSongAt(userId)
                match lastSong with
                | Some last when (now - last).TotalSeconds < float conf.SongCooldownSeconds ->
                    return MediaOutcome.Refused "рано, дай отдышаться — попробуй чуть позже"
                | _ ->
                    let prompt =
                        match styleHint with
                        | Some style -> $"Style: {style}\n\n{lyricsOrDesc}"
                        | None -> lyricsOrDesc
                    match! musicGen.Generate(prompt, ctx, CancellationToken.None) with
                    | Error err ->
                        let reason =
                            if LlmError.isTransient err then
                                "Модель перегружена, попробуй сочинить чуть позже 🙏"
                            else
                                "Не получилось сочинить 🙁"
                        return MediaOutcome.GenFailed reason
                    | Ok(bytes, _usage) when bytes.Length = 0 ->
                        logger.LogWarning("Music generation returned no audio")
                        return MediaOutcome.GenFailed "Не получилось сочинить 🙁"
                    | Ok(bytes, _usage) ->
                        let! caption = composeCaption logger chat conf ctx "песню" lyricsOrDesc
                        let! mp3Bytes = tryConvertToMp3 logger bytes
                        let deliverBytes = mp3Bytes |> Option.defaultValue bytes
                        let title = if caption.Length > SongTitleMaxLen then caption.Substring(0, SongTitleMaxLen) else caption
                        let! sent = BotHelpers.sendAudioReplyWithTitle tg chatId deliverBytes title replyToMessageId
                        do! db.RecordSong(userId, now)
                        return MediaOutcome.Sent(sent, caption)
        }

    /// Core of `/say` (command AND `speak_text` tool, S10 PR2): guardrail (SAY_MAX_CHARS)
    /// -> `speech.Synthesize` -> re-encode to ogg/opus (best-effort) -> `sendVoiceReply`,
    /// falling back to `sendAudioReply` when neither the raw bytes nor a converted copy is
    /// a proper Ogg container. Unlike `generateImage`/`generateSong`, this does NOT call
    /// `composeCaption` — the spoken `text` IS the content (there is nothing to react to
    /// that isn't already the text itself), so the returned "caption" is `text` verbatim,
    /// matching the existing `[voice] {text}` message_log convention.
    let speakText
        (logger: ILogger)
        (speech: ISpeech)
        (tg: ITelegramApi)
        (conf: BotConfiguration)
        (chatId: int64)
        (replyToMessageId: int64)
        (voice: string option)
        (text: string)
        (ctx: UsageContext)
        : Task<MediaOutcome> =
        task {
            if String.IsNullOrWhiteSpace text then
                return MediaOutcome.Refused "Скажи, что озвучить: `/say привет`."
            elif text.Length > conf.SayMaxChars then
                return MediaOutcome.Refused $"Слишком длинный текст — максимум {conf.SayMaxChars} символов."
            else
                match! speech.Synthesize(text, voice, ctx, CancellationToken.None) with
                | Error err ->
                    logger.LogWarning("/say: TTS synthesis failed: {Error}", string err)
                    return MediaOutcome.GenFailed "Не получилось озвучить 🙁"
                | Ok bytes when bytes.Length = 0 ->
                    logger.LogWarning("/say: TTS synthesis returned no audio")
                    return MediaOutcome.GenFailed "Не получилось озвучить 🙁"
                | Ok bytes ->
                    let! oggBytes = if isOggContainer bytes then Task.FromResult(Some bytes) else tryConvertToOggOpus logger bytes
                    match oggBytes with
                    | Some ogg ->
                        let! sent = BotHelpers.sendVoiceReply tg chatId ogg replyToMessageId
                        return MediaOutcome.Sent(sent, text)
                    | None ->
                        let! sent = BotHelpers.sendAudioReply tg chatId bytes replyToMessageId
                        return MediaOutcome.Sent(sent, text)
        }
