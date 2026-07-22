namespace AlitaBot.Services

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

                    // Best-effort "typing…" while tools run — droppable (CallIgnore logs a
                    // Warning and swallows any Telegram-side rejection, never blocks the loop).
                    do! tg.CallIgnore(Req.SendChatAction.Make(execCtx.ChatId, ChatAction.Typing))

                    let! toolMessages =
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
                                            return { ResultText = "Инструмент временно недоступен."; Outcome = "tool_exception" }
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
                                return toolMsg
                            })
                        |> Task.WhenAll

                    messages <- messages @ [ assistantMsg ] @ (toolMessages |> Array.toList)
                | _ -> final <- Some { result with Usage = totalUsage }

            return final.Value
        }
