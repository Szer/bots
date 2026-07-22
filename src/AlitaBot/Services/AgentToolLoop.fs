namespace AlitaBot.Services

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Funogram.Telegram.Types
open AlitaBot
open AlitaBot.Llm
open BotInfra

module Req = Funogram.Telegram.Req

/// The natural-language tool-calling loop (S10 PR1) — reuses `IReplyRenderer.Render`
/// UNMODIFIED, once per round: attach tool defs, `chat.CompleteStream`, hand the chunk
/// stream to the SAME renderer the non-tool path uses. A pure tool-call round has empty
/// `delta.content` -> the renderer's existing "empty text -> send nothing" branch (guarded
/// for `FinishReason.ToolCalls`, see ReplyRenderer.fs) does the right thing: no Telegram
/// message, `FinalMessage = None`. The loop executes the requested tools, appends an
/// Assistant message (with ToolCalls) plus one Tool-role message per result, and starts the
/// next round. The FINAL round (no tool calls requested) streams through the renderer
/// exactly like today — the persona reply/caption gets normal streaming UX for free.
type AgentToolLoop
    (
        chat: IChatCompletion,
        executor: IToolExecutor,
        renderers: ReplyRendererFactory,
        tg: ITelegramApi,
        options: IOptions<BotConfiguration>,
        logger: ILogger<AgentToolLoop>
    ) =

    let addUsage (a: TokenUsage option) (b: TokenUsage option) : TokenUsage option =
        match a, b with
        | Some x, Some y ->
            Some
                { PromptTokens = x.PromptTokens + y.PromptTokens
                  CompletionTokens = x.CompletionTokens + y.CompletionTokens
                  TotalTokens = x.TotalTokens + y.TotalTokens }
        | Some x, None -> Some x
        | None, y -> y

    /// Normalizes text for the duplicate-final-reply guard below — case-insensitive,
    /// trims whitespace and a small set of trailing punctuation ("Готово" / "Готово." /
    /// "готово!" all normalize the same) but does NO fuzzy/substring matching: only a
    /// genuinely (near-)identical caption counts as a duplicate, so a legitimate follow-up
    /// that happens to start the same way is never touched.
    let normalizeForDuplicateCheck (s: string) =
        s.Trim().TrimEnd('.', '!', '?', '…', ',', ';', ':', ' ').ToLowerInvariant()

    /// Runs the loop for one turn, starting from `baseRequest` (its `Tools` are the full
    /// offered set — [] when NL tools are off/unavailable for this caller). Returns the
    /// SAME `RenderResult` shape a non-tool `Respond` call would, with `Usage` summed across
    /// every round.
    member _.Run(baseRequest: ChatRequest, execCtx: ToolExecContext, streamMode: string, ct: CancellationToken) : Task<RenderResult> =
        task {
            let conf = options.Value
            let renderer = renderers.ForMode streamMode
            let mutable messages = baseRequest.Messages
            let mutable totalUsage: TokenUsage option = None
            let mutable iteration = 0
            let mutable final: RenderResult option = None
            // (toolName, caption) for every media tool that has ALREADY delivered its
            // result with a caption THIS turn — S10 PR1 staging finding (Bug 2): the
            // loop's own final round could echo/paraphrase the same caption as a second,
            // separate text reply. Feeds the duplicate-final-reply guard below.
            let mutable capturedCaptions: (string * string) list = []

            while final.IsNone do
                iteration <- iteration + 1
                // Past the iteration cap: strip Tools so the model is forced into a clean
                // final text answer instead of looping forever.
                let toolsForRound = if iteration > conf.NlToolsMaxIterations then [] else baseRequest.Tools
                let request = { baseRequest with Messages = messages; Tools = toolsForRound }
                let chunks = chat.CompleteStream(request, execCtx.UsageCtx, ct)
                let! result = renderer.Render(execCtx.ChatId, execCtx.ReplyToMessageId, chunks, ct)

                totalUsage <- addUsage totalUsage result.Usage

                match result.Outcome with
                | RenderOutcome.Completed FinishReason.ToolCalls when not result.ToolCalls.IsEmpty && iteration <= conf.NlToolsMaxIterations ->
                    let assistantMsg: ChatMessage =
                        { Role = ChatRole.Assistant
                          Content = (if result.FullText = "" then [] else [ ContentPart.Text result.FullText ])
                          ToolCalls = result.ToolCalls
                          ToolCallId = None }

                    // S10 PR1 staging finding (Bug 3): a single SendChatAction call goes
                    // stale after ~5s (Telegram's own indicator lifetime), but image
                    // generation regularly takes 10-15s+ — the typing indicator visibly
                    // died mid-generation. Runs a periodic best-effort refresher (~4s
                    // cadence, fires immediately then on each tick) for the duration of
                    // THIS round's tool execution instead of firing once; upload_photo
                    // when generate_image is among this round's calls (the more accurate
                    // Telegram indicator for "sending a photo"), typing otherwise.
                    // CallIgnore already Warning-logs+swallows any Telegram-side
                    // rejection — Task.Delay's cancellation on stop is the only expected
                    // "failure" here, and it's swallowed too.
                    let chatAction =
                        if result.ToolCalls |> List.exists (fun c -> c.Name = "generate_image") then
                            ChatAction.UploadPhoto
                        else
                            ChatAction.Typing

                    use typingCts = new CancellationTokenSource()
                    let typingLoop =
                        task {
                            try
                                while true do
                                    do! tg.CallIgnore(Req.SendChatAction.Make(execCtx.ChatId, chatAction))
                                    do! Task.Delay(TimeSpan.FromSeconds 4.0, typingCts.Token)
                            with :? OperationCanceledException -> ()
                        }

                    let! toolOutcomes =
                        result.ToolCalls
                        |> List.map (fun call ->
                            task {
                                let! execResult =
                                    task {
                                        try
                                            return! executor.Execute(call.Name, call.ArgumentsJson, execCtx, ct)
                                        with ex ->
                                            logger.LogWarning(
                                                ex,
                                                "Tool call {Tool} threw — reporting failure to the model instead of crashing the turn",
                                                call.Name)
                                            return
                                                { ResultText = "Инструмент временно недоступен."
                                                  Outcome = "tool_exception"
                                                  CaptionSent = None }
                                    }
                                Metrics.toolCallTotal.Add(
                                    1L,
                                    KeyValuePair("tool", box call.Name),
                                    KeyValuePair("outcome", box execResult.Outcome))
                                let toolMsg: ChatMessage =
                                    { Role = ChatRole.Tool
                                      Content = [ ContentPart.Text execResult.ResultText ]
                                      ToolCalls = []
                                      ToolCallId = Some call.Id }
                                return toolMsg, (execResult.CaptionSent |> Option.map (fun c -> call.Name, c))
                            })
                        |> Task.WhenAll

                    // Stop the refresher now that every tool call in this round has
                    // returned, then await it so its (swallowed) cancellation is fully
                    // observed before moving on.
                    typingCts.Cancel()
                    do! typingLoop

                    let toolMessages = toolOutcomes |> Array.map fst
                    capturedCaptions <- capturedCaptions @ (toolOutcomes |> Array.choose snd |> Array.toList)

                    messages <- messages @ [ assistantMsg ] @ (toolMessages |> Array.toList)
                | _ ->
                    // S10 PR1 staging finding (Bug 2, belt-and-braces layer): TOOL_USE_PROMPT
                    // now instructs the model to stay silent after a media tool already
                    // delivered its caption — this is the deterministic backstop in case it
                    // doesn't listen. By the time we can see `result.FullText`, the renderer
                    // has ALREADY sent/streamed it (every IReplyRenderer sends progressively
                    // or at completion, never deferred) — "suppressing the send" here means
                    // deleting the just-sent message and reporting FinalMessage=None instead,
                    // so ResponderService/BotService treat this exactly like "no reply" (no
                    // message_log row, no cost footer). Only a clear (near-)exact duplicate of
                    // a caption sent THIS turn is suppressed — anything else, including
                    // genuinely new follow-up text, ships untouched.
                    let duplicateCaption =
                        if result.FullText = "" then
                            None
                        else
                            let normalizedFinal = normalizeForDuplicateCheck result.FullText
                            capturedCaptions
                            |> List.tryFind (fun (_, caption) -> normalizeForDuplicateCheck caption = normalizedFinal)

                    match duplicateCaption, result.FinalMessage with
                    | Some(toolName, _), Some sent ->
                        logger.LogDebug(
                            "Suppressing final reply {Text} — duplicates the caption {Tool} already sent this turn",
                            result.FullText,
                            toolName)
                        do! BotHelpers.deleteMessage tg execCtx.ChatId sent.MessageId
                        Metrics.toolCallTotal.Add(
                            1L,
                            KeyValuePair("tool", box toolName),
                            KeyValuePair("outcome", box "duplicate_final_suppressed"))
                        final <- Some { result with FinalMessage = None; FullText = ""; Usage = totalUsage }
                    | _ -> final <- Some { result with Usage = totalUsage }

            return final.Value
        }
