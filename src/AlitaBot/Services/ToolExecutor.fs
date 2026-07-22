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

/// Dispatch + guardrails for the NL tool-calling loop's tools (PR1: generate_image,
/// web_search) — the "tool layer", distinct from the command layer (BotService's `/img`
/// etc.), even though both ultimately share MediaActions' core.
type ToolExecutorService
    (
        imageGen: IImageGen,
        chat: IChatCompletion,
        webSearch: IWebSearch,
        db: DbService,
        embeddings: IEmbeddings,
        tg: ITelegramApi,
        options: IOptions<BotConfiguration>,
        time: TimeProvider,
        logger: ILogger<ToolExecutorService>
    ) =

    /// llm_usage `kind` values counted against the per-user hourly rate limit for
    /// cost-heavy tools (PR2 adds "music" once generate_song reaches the tool layer).
    let rateLimitedKinds = [ "image"; "web_search" ]

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

    interface IToolExecutor with
        member _.Execute(name: string, argumentsJson: string, ctx: ToolExecContext, ct: CancellationToken) =
            match name with
            | "generate_image" -> execGenerateImage argumentsJson ctx ct
            | "web_search" -> execWebSearch argumentsJson ctx ct
            | other ->
                Task.FromResult
                    { ResultText = $"Неизвестный инструмент: {other}"
                      Outcome = "unknown_tool"
                      CaptionSent = None }
