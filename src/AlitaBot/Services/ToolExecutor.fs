namespace AlitaBot.Services

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open AlitaBot
open AlitaBot.Llm
open BotInfra

/// Per-turn context a tool call executes under (S10 PR1) — supplied by ResponderService/
/// AgentToolLoop, never by the model itself.
type ToolExecContext =
    { ChatId: int64
      ReplyToMessageId: int64
      UserId: int64
      /// The invoking user's display name (S10 PR2) — needed by `roast_user`'s target
      /// resolution when the target is the invoker themselves (mirrors the command path's
      /// `displayNameOf from`, which a tool call has no `User` object to derive from).
      UserDisplayName: string
      IsAdmin: bool
      UsageCtx: UsageContext
      /// Pre-fetched by ResponderService from the TRIGGERING message's reply target — the
      /// model never supplies this (prompt-injection surface avoided); mirrors the `/img`
      /// command path's own tryFetchReplySourceImage call.
      SourceImage: byte[] option }

/// Result of one tool execution — always a value, never an exception (the loop feeds
/// `ResultText` back to the model as Tool-role content regardless of success/failure, so
/// denials/errors are phrased for the persona to react to in character).
type ToolExecResult =
    { ResultText: string
      /// "ok" | "denied_cooldown" | "denied_disabled" | "denied_rate_limit" |
      /// "denied_admin" | "gen_failed" | "bad_arguments" | "unknown_tool" | "tool_exception"
      Outcome: string
      /// Set to the ACTUAL caption text (MediaActions.composeCaption's output) when this
      /// call delivered media with a caption already attached (S10 PR1 staging finding:
      /// the loop's own final round could echo/paraphrase this same caption as a second,
      /// separate text reply) — AgentToolLoop threads this through to a deterministic
      /// duplicate-final-reply guard. `None` for every non-media tool/outcome.
      CaptionSent: string option }

type IToolExecutor =
    abstract Execute : name: string * argumentsJson: string * ctx: ToolExecContext * ct: CancellationToken -> Task<ToolExecResult>

/// Lenient JSON argument extraction — a malformed/missing field never throws, callers turn
/// `None` into a `bad_arguments` ToolExecResult that asks the model to re-ask instead of
/// crashing the turn.
module internal ToolArgs =
    let tryStringField (argumentsJson: string) (field: string) : string option =
        try
            use doc = JsonDocument.Parse(argumentsJson: string)
            if doc.RootElement.ValueKind <> JsonValueKind.Object then
                None
            else
                match doc.RootElement.TryGetProperty field with
                | true, v when v.ValueKind = JsonValueKind.String && not (String.IsNullOrWhiteSpace(v.GetString())) ->
                    Some(v.GetString())
                | _ -> None
        with _ -> None

    /// `summarize_chat`'s optional `count` — a JSON number, unlike every other tool's
    /// string arguments, so it gets its own lenient extractor.
    let tryIntField (argumentsJson: string) (field: string) : int option =
        try
            use doc = JsonDocument.Parse(argumentsJson: string)
            if doc.RootElement.ValueKind <> JsonValueKind.Object then
                None
            else
                match doc.RootElement.TryGetProperty field with
                | true, v when v.ValueKind = JsonValueKind.Number -> Some(v.GetInt32())
                | _ -> None
        with _ -> None

/// Maps a `CommandCores` core's granular command-outcome string (e.g. "dossier_shown",
/// "roast_cooldown", "sql_refused_non_admin" — the same strings BotService's
/// `countOutcome` telemetry uses) down to the small, controlled `ToolExecResult.Outcome`
/// vocabulary (S10 PR2) — keeps `alitabot_tool_call_total`'s cardinality bounded the same
/// way PR1's two tools already do, without needing every read-only core to know about the
/// tool layer's vocabulary.
module internal ToolOutcome =
    let classify (commandOutcome: string) : string =
        if commandOutcome.Contains "cooldown" then "denied_cooldown"
        elif commandOutcome.Contains "non_admin" then "denied_admin"
        elif commandOutcome.Contains "refused" || commandOutcome.Contains "rejected" || commandOutcome.Contains "unknown_user" || commandOutcome.Contains "unknown_target" then
            "denied_disabled"
        elif commandOutcome.Contains "empty_question" || commandOutcome.Contains "empty_prompt" then
            "bad_arguments"
        elif commandOutcome.Contains "failed" then "gen_failed"
        else "ok"

/// Dispatch + guardrails for the NL tool-calling loop's tools (PR1: generate_image,
/// web_search) — the "tool layer", distinct from the command layer (BotService's `/img`
/// etc.), even though both ultimately share MediaActions' core.
type ToolExecutorService
    (
        imageGen: IImageGen,
        chat: IChatCompletion,
        webSearch: IWebSearch,
        musicGen: IMusicGen,
        speech: ISpeech,
        db: DbService,
        embeddings: IEmbeddings,
        tg: ITelegramApi,
        settingsReloader: ISettingsReloader,
        options: IOptions<BotConfiguration>,
        time: TimeProvider,
        logger: ILogger<ToolExecutorService>
    ) =

    /// llm_usage `kind` values counted against the per-user hourly rate limit for
    /// cost-heavy tools (S10 PR2: "music" joins generate_image/web_search's shared bucket
    /// now that generate_song reaches the tool layer — speak_text and the read-only tools
    /// are NOT rate-limited here, mirroring their command paths, which have no cooldown
    /// either beyond a length cap).
    let rateLimitedKinds = [ "image"; "web_search"; "music" ]

    let underRateLimit (conf: BotConfiguration) (userId: int64) : Task<bool> =
        task {
            let since = time.GetUtcNow().UtcDateTime.AddHours(-1.0)
            let! count = db.ToolCallCountSince(userId, rateLimitedKinds, since)
            return count < int64 conf.NlToolsRateLimitPerHour
        }

    /// Logs the bot's own media-tool reply to message_log (mirroring the command path's
    /// `logAndEmbed` call) — the loop itself never touches Telegram/message_log for tool
    /// media, per the plan; the executor does this bookkeeping on the tool's behalf.
    let logMediaReply (conf: BotConfiguration) (ctx: ToolExecContext) (sentMessageId: int64) (logText: string) : Task<unit> =
        task {
            let botUserId = BotHelpers.botUserId conf
            let row =
                MessageLog.logRow time ctx.ChatId sentMessageId botUserId conf.BotUsername conf.BotUsername true
                    (Some ctx.ReplyToMessageId) logText
            let! _ = MessageLog.logAndEmbed logger embeddings db conf row
            return ()
        }

    let execGenerateImage (argumentsJson: string) (ctx: ToolExecContext) (_ct: CancellationToken) : Task<ToolExecResult> =
        task {
            let conf = options.Value
            match ToolArgs.tryStringField argumentsJson "prompt" with
            | None ->
                return
                    { ResultText = "Не поняла, что рисовать — переспроси у пользователя описание картинки."
                      Outcome = "bad_arguments"
                      CaptionSent = None }
            | Some prompt ->
                let! ok = underRateLimit conf ctx.UserId
                if not ok then
                    return
                        { ResultText = "Слишком много картинок за последний час — предложи подождать и попробовать позже."
                          Outcome = "denied_rate_limit"
                          CaptionSent = None }
                else
                    match!
                        MediaActions.generateImage
                            logger imageGen chat tg conf ctx.ChatId ctx.ReplyToMessageId ctx.SourceImage prompt ctx.UsageCtx
                    with
                    | MediaOutcome.Sent(sent, caption) ->
                        do! logMediaReply conf ctx sent.MessageId $"[image] {caption}"
                        return
                            { ResultText = $"Изображение отправлено. Подпись: «{caption}»"
                              Outcome = "ok"
                              CaptionSent = Some caption }
                    | MediaOutcome.Refused reason ->
                        return { ResultText = reason; Outcome = "denied_disabled"; CaptionSent = None }
                    | MediaOutcome.GenFailed reason ->
                        return { ResultText = reason; Outcome = "gen_failed"; CaptionSent = None }
        }

    let execWebSearch (argumentsJson: string) (ctx: ToolExecContext) (ct: CancellationToken) : Task<ToolExecResult> =
        task {
            match ToolArgs.tryStringField argumentsJson "query" with
            | None ->
                return
                    { ResultText = "Не поняла запрос для поиска — переспроси, что искать."
                      Outcome = "bad_arguments"
                      CaptionSent = None }
            | Some query ->
                let conf = options.Value
                let! ok = underRateLimit conf ctx.UserId
                if not ok then
                    return
                        { ResultText = "Слишком много поисков за последний час — предложи подождать и попробовать позже."
                          Outcome = "denied_rate_limit"
                          CaptionSent = None }
                else
                    match! webSearch.Search(query, ctx.UsageCtx, ct) with
                    | Ok text -> return { ResultText = text; Outcome = "ok"; CaptionSent = None }
                    | Error err ->
                        logger.LogWarning("web_search tool call failed: {Error}", string err)
                        return { ResultText = "Поиск временно недоступен."; Outcome = "gen_failed"; CaptionSent = None }
        }

    // ── S10 PR2: generate_song, speak_text, sql_query (AdminOnly), read-only tools ─────

    let execGenerateSong (argumentsJson: string) (ctx: ToolExecContext) (_ct: CancellationToken) : Task<ToolExecResult> =
        task {
            let conf = options.Value
            match ToolArgs.tryStringField argumentsJson "lyrics_or_description" with
            | None ->
                return
                    { ResultText = "Не поняла, что сочинять — переспроси у пользователя текст или описание песни."
                      Outcome = "bad_arguments"
                      CaptionSent = None }
            | Some lyricsOrDesc ->
                // Shares generate_image/web_search's hourly bucket (rateLimitedKinds now
                // includes "music") — one combined cost-heavy-tool limit per user.
                let! ok = underRateLimit conf ctx.UserId
                if not ok then
                    return
                        { ResultText = "Слишком много запросов за последний час — предложи подождать и попробовать позже."
                          Outcome = "denied_rate_limit"
                          CaptionSent = None }
                else
                    let styleHint = ToolArgs.tryStringField argumentsJson "style"
                    match!
                        MediaActions.generateSong
                            logger musicGen chat tg db time conf ctx.ChatId ctx.ReplyToMessageId ctx.UserId styleHint
                            lyricsOrDesc ctx.UsageCtx
                    with
                    | MediaOutcome.Sent(sent, caption) ->
                        do! logMediaReply conf ctx sent.MessageId $"[song] {caption}"
                        return
                            { ResultText = $"Песня отправлена. Реакция: «{caption}»"
                              Outcome = "ok"
                              CaptionSent = Some caption }
                    | MediaOutcome.Refused reason ->
                        return { ResultText = reason; Outcome = "denied_disabled"; CaptionSent = None }
                    | MediaOutcome.GenFailed reason ->
                        return { ResultText = reason; Outcome = "gen_failed"; CaptionSent = None }
        }

    let execSpeakText (argumentsJson: string) (ctx: ToolExecContext) (_ct: CancellationToken) : Task<ToolExecResult> =
        task {
            let conf = options.Value
            match ToolArgs.tryStringField argumentsJson "text" with
            | None ->
                return
                    { ResultText = "Не поняла, что озвучить — переспроси у пользователя текст."
                      Outcome = "bad_arguments"
                      CaptionSent = None }
            | Some text ->
                let voice = ToolArgs.tryStringField argumentsJson "voice"
                match! MediaActions.speakText logger speech tg conf ctx.ChatId ctx.ReplyToMessageId voice text ctx.UsageCtx with
                | MediaOutcome.Sent(sent, spokenText) ->
                    do! logMediaReply conf ctx sent.MessageId $"[voice] {spokenText}"
                    return { ResultText = "Голосовое сообщение отправлено."; Outcome = "ok"; CaptionSent = Some spokenText }
                | MediaOutcome.Refused reason ->
                    return { ResultText = reason; Outcome = "denied_disabled"; CaptionSent = None }
                | MediaOutcome.GenFailed reason ->
                    return { ResultText = reason; Outcome = "gen_failed"; CaptionSent = None }
        }

    /// `sql_query` — AdminOnly=true keeps this invisible to a non-admin caller's tools
    /// array (ToolRegistry.availableToolDefs), but `ctx.IsAdmin` is checked again here,
    /// belt-and-braces, exactly like `/sql`'s own `Admin.isAdmin` gate.
    let execSqlQuery (argumentsJson: string) (ctx: ToolExecContext) (_ct: CancellationToken) : Task<ToolExecResult> =
        task {
            let conf = options.Value
            match ToolArgs.tryStringField argumentsJson "question" with
            | None ->
                return
                    { ResultText = "Не поняла вопрос для SQL — переспроси, что нужно узнать."
                      Outcome = "bad_arguments"
                      CaptionSent = None }
            | Some question ->
                let! text, outcome = CommandCores.sqlCore db chat logger conf ctx.IsAdmin ctx.UsageCtx question
                return { ResultText = text; Outcome = ToolOutcome.classify outcome; CaptionSent = None }
        }

    let execAskChatHistory (argumentsJson: string) (ctx: ToolExecContext) (_ct: CancellationToken) : Task<ToolExecResult> =
        task {
            let conf = options.Value
            match ToolArgs.tryStringField argumentsJson "question" with
            | None ->
                return
                    { ResultText = "Не поняла вопрос — переспроси, что нужно найти в истории чата."
                      Outcome = "bad_arguments"
                      CaptionSent = None }
            | Some question ->
                let! text, outcome = CommandCores.askCore db embeddings chat logger conf ctx.ChatId ctx.UsageCtx question
                return { ResultText = text; Outcome = ToolOutcome.classify outcome; CaptionSent = None }
        }

    let execSummarizeChat (argumentsJson: string) (ctx: ToolExecContext) (_ct: CancellationToken) : Task<ToolExecResult> =
        task {
            let conf = options.Value
            let countArg = ToolArgs.tryIntField argumentsJson "count" |> Option.map string |> Option.defaultValue ""
            let! text, outcome = CommandCores.summaryCore db chat logger conf ctx.ChatId ctx.UsageCtx countArg
            return { ResultText = text; Outcome = ToolOutcome.classify outcome; CaptionSent = None }
        }

    let execShowDossier (argumentsJson: string) (ctx: ToolExecContext) (_ct: CancellationToken) : Task<ToolExecResult> =
        task {
            let target = ToolArgs.tryStringField argumentsJson "target" |> Option.defaultValue ""
            let! text, outcome = CommandCores.dossierCore db ctx.UserId target
            return { ResultText = text; Outcome = ToolOutcome.classify outcome; CaptionSent = None }
        }

    let execRoastUser (argumentsJson: string) (ctx: ToolExecContext) (_ct: CancellationToken) : Task<ToolExecResult> =
        task {
            let conf = options.Value
            let targetArg = ToolArgs.tryStringField argumentsJson "target"
            match! CommandCores.resolveRoastTargetForTool db ctx.UserId ctx.UserDisplayName targetArg with
            | None -> return { ResultText = CommandCores.NoRoastDataText; Outcome = "denied_disabled"; CaptionSent = None }
            | Some target ->
                let! text, outcome = CommandCores.roastCore db chat logger time conf ctx.UsageCtx target
                return { ResultText = text; Outcome = ToolOutcome.classify outcome; CaptionSent = None }
        }

    let execShowAwards (_argumentsJson: string) (ctx: ToolExecContext) (_ct: CancellationToken) : Task<ToolExecResult> =
        task {
            let conf = options.Value
            let! text, outcome = CommandCores.awardsCore db chat logger time conf ctx.ChatId ctx.UsageCtx
            return { ResultText = text; Outcome = ToolOutcome.classify outcome; CaptionSent = None }
        }

    let execShowQuote (_argumentsJson: string) (ctx: ToolExecContext) (_ct: CancellationToken) : Task<ToolExecResult> =
        task {
            let conf = options.Value
            let! text, outcome = CommandCores.quoteCore db chat logger time conf ctx.ChatId ctx.UsageCtx
            return { ResultText = text; Outcome = ToolOutcome.classify outcome; CaptionSent = None }
        }

    let execShowKarma (argumentsJson: string) (ctx: ToolExecContext) (_ct: CancellationToken) : Task<ToolExecResult> =
        task {
            let target = ToolArgs.tryStringField argumentsJson "target" |> Option.defaultValue ""
            let! text, outcome = CommandCores.karmaCore db ctx.UserId target
            return { ResultText = text; Outcome = ToolOutcome.classify outcome; CaptionSent = None }
        }

    let execSwitchModel (argumentsJson: string) (ctx: ToolExecContext) (_ct: CancellationToken) : Task<ToolExecResult> =
        task {
            let conf = options.Value
            let modelArg = ToolArgs.tryStringField argumentsJson "model" |> Option.defaultValue ""
            let! text, outcome = CommandCores.modelCore db settingsReloader conf modelArg
            return { ResultText = text; Outcome = ToolOutcome.classify outcome; CaptionSent = None }
        }

    let execShowUsage (_argumentsJson: string) (ctx: ToolExecContext) (_ct: CancellationToken) : Task<ToolExecResult> =
        task {
            let conf = options.Value
            let! text, outcome = CommandCores.usageCore db time conf
            return { ResultText = text; Outcome = ToolOutcome.classify outcome; CaptionSent = None }
        }

    interface IToolExecutor with
        member _.Execute(name: string, argumentsJson: string, ctx: ToolExecContext, ct: CancellationToken) =
            match name with
            | "generate_image" -> execGenerateImage argumentsJson ctx ct
            | "web_search" -> execWebSearch argumentsJson ctx ct
            | "generate_song" -> execGenerateSong argumentsJson ctx ct
            | "speak_text" -> execSpeakText argumentsJson ctx ct
            | "sql_query" -> execSqlQuery argumentsJson ctx ct
            | "ask_chat_history" -> execAskChatHistory argumentsJson ctx ct
            | "summarize_chat" -> execSummarizeChat argumentsJson ctx ct
            | "show_dossier" -> execShowDossier argumentsJson ctx ct
            | "roast_user" -> execRoastUser argumentsJson ctx ct
            | "show_awards" -> execShowAwards argumentsJson ctx ct
            | "show_quote" -> execShowQuote argumentsJson ctx ct
            | "show_karma" -> execShowKarma argumentsJson ctx ct
            | "switch_model" -> execSwitchModel argumentsJson ctx ct
            | "show_usage" -> execShowUsage argumentsJson ctx ct
            | other ->
                Task.FromResult
                    { ResultText = $"Неизвестный инструмент: {other}"
                      Outcome = "unknown_tool"
                      CaptionSent = None }
