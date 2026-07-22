namespace AlitaBot.Services

open System
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
