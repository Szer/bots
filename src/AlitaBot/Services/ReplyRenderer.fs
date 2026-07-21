namespace AlitaBot.Services

open System
open System.Collections.Generic
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Funogram.Telegram.Types
open AlitaBot.Llm
open BotInfra

[<RequireQualifiedAccess>]
type RenderOutcome =
    | Completed of FinishReason
    | Failed of LlmError

/// What a renderer delivered: the last Telegram message it sent (if any) and the
/// text that ended up visible in the chat (for message_log).
type RenderResult =
    { FinalMessage: Message option
      FullText: string
      Outcome: RenderOutcome }

/// Turns a chunk stream into Telegram message(s). Failure policy shared by all
/// implementations: ContentFiltered before any text → fixed RU reply; any other
/// failure before text → silence (Warning log); failure after text started →
/// finalize the partial text.
type IReplyRenderer =
    abstract Render:
        chatId: int64 * replyToMessageId: int64 * chunks: IAsyncEnumerable<ChatChunk> * ct: CancellationToken ->
            Task<RenderResult>

module ReplyRenderer =
    [<Literal>]
    let ContentFilteredReply = "ну и дичь ты написал, даже мой фильтр офигел 🤷"

/// Accumulates the whole stream, sends a single reply at the end.
type PlainRenderer(tg: ITelegramApi, logger: ILogger<PlainRenderer>) =

    interface IReplyRenderer with
        member _.Render(chatId, replyToMessageId, chunks, ct) =
            task {
                let text = StringBuilder()
                let mutable outcome = RenderOutcome.Completed FinishReason.Stop
                use enumerator = chunks.GetAsyncEnumerator(ct)
                let mutable go = true
                while go do
                    let! has = enumerator.MoveNextAsync()
                    if not has then
                        go <- false
                    else
                        match enumerator.Current with
                        | ChatChunk.TextDelta t -> %text.Append(t)
                        | ChatChunk.ToolCallDelta _ -> ()
                        | ChatChunk.Completed response -> outcome <- RenderOutcome.Completed response.FinishReason
                        | ChatChunk.Failed err -> outcome <- RenderOutcome.Failed err

                let fullText = text.ToString()
                match outcome with
                | RenderOutcome.Failed(LlmError.ContentFiltered _) when fullText.Length = 0 ->
                    let! sent = BotHelpers.sendTextReply tg chatId ReplyRenderer.ContentFilteredReply replyToMessageId
                    return { FinalMessage = Some sent; FullText = ReplyRenderer.ContentFilteredReply; Outcome = outcome }
                | RenderOutcome.Failed err when fullText.Length = 0 ->
                    logger.LogWarning("LLM stream failed before any text — staying silent: {Error}", string err)
                    return { FinalMessage = None; FullText = ""; Outcome = outcome }
                | _ when fullText.Length = 0 ->
                    logger.LogWarning("LLM stream completed with empty text — nothing to send")
                    return { FinalMessage = None; FullText = ""; Outcome = outcome }
                | _ ->
                    match outcome with
                    | RenderOutcome.Failed err ->
                        logger.LogWarning("LLM stream failed after partial text — finalizing partial reply: {Error}", string err)
                    | _ -> ()
                    let! sent = BotHelpers.sendTextReply tg chatId fullText replyToMessageId
                    return { FinalMessage = Some sent; FullText = fullText; Outcome = outcome }
            }

/// Streams by editing one message: sends on the first meaningful chunk, then edits
/// when ≥1.5s elapsed AND ≥40 new chars accumulated, plus a final edit at completion.
type EditThrottleRenderer(tg: ITelegramApi, time: TimeProvider, logger: ILogger<EditThrottleRenderer>) =
    let minEditInterval = TimeSpan.FromSeconds 1.5
    let minNewChars = 40

    interface IReplyRenderer with
        member _.Render(chatId, replyToMessageId, chunks, ct) =
            task {
                let text = StringBuilder()
                let mutable outcome = RenderOutcome.Completed FinishReason.Stop
                let mutable sentMsg: Message option = None
                let mutable lastSentLen = 0
                let mutable lastSentAt = time.GetTimestamp()

                use enumerator = chunks.GetAsyncEnumerator(ct)
                let mutable go = true
                while go do
                    let! has = enumerator.MoveNextAsync()
                    if not has then
                        go <- false
                    else
                        match enumerator.Current with
                        | ChatChunk.TextDelta t when t.Length > 0 ->
                            %text.Append(t)
                            match sentMsg with
                            | None ->
                                let! sent = BotHelpers.sendTextReply tg chatId (text.ToString()) replyToMessageId
                                sentMsg <- Some sent
                                lastSentLen <- text.Length
                                lastSentAt <- time.GetTimestamp()
                            | Some m ->
                                if time.GetElapsedTime(lastSentAt) >= minEditInterval
                                   && text.Length - lastSentLen >= minNewChars then
                                    do! BotHelpers.editMessageText tg chatId m.MessageId (text.ToString())
                                    lastSentLen <- text.Length
                                    lastSentAt <- time.GetTimestamp()
                        | ChatChunk.TextDelta _
                        | ChatChunk.ToolCallDelta _ -> ()
                        | ChatChunk.Completed response -> outcome <- RenderOutcome.Completed response.FinishReason
                        | ChatChunk.Failed err -> outcome <- RenderOutcome.Failed err

                let fullText = text.ToString()
                match sentMsg with
                | None ->
                    match outcome with
                    | RenderOutcome.Failed(LlmError.ContentFiltered _) ->
                        let! sent = BotHelpers.sendTextReply tg chatId ReplyRenderer.ContentFilteredReply replyToMessageId
                        return { FinalMessage = Some sent; FullText = ReplyRenderer.ContentFilteredReply; Outcome = outcome }
                    | RenderOutcome.Failed err ->
                        logger.LogWarning("LLM stream failed before any text — staying silent: {Error}", string err)
                        return { FinalMessage = None; FullText = ""; Outcome = outcome }
                    | RenderOutcome.Completed _ ->
                        logger.LogWarning("LLM stream completed with empty text — nothing to send")
                        return { FinalMessage = None; FullText = ""; Outcome = outcome }
                | Some m ->
                    match outcome with
                    | RenderOutcome.Failed err ->
                        logger.LogWarning("LLM stream failed after partial text — finalizing partial reply: {Error}", string err)
                    | _ -> ()
                    if fullText.Length <> lastSentLen then
                        do! BotHelpers.editMessageText tg chatId m.MessageId fullText
                    return { FinalMessage = Some m; FullText = fullText; Outcome = outcome }
            }

/// TODO(M5): real sendMessageDraft rendering needs probing against real Telegram
/// (draft semantics in groups unverified) — until then it delegates to edit-throttle.
type DraftRenderer(fallback: EditThrottleRenderer) =
    interface IReplyRenderer with
        member _.Render(chatId, replyToMessageId, chunks, ct) =
            (fallback :> IReplyRenderer).Render(chatId, replyToMessageId, chunks, ct)

/// Selects a renderer for a STREAM_MODE value ("plain" | "edit" | "draft").
type ReplyRendererFactory(tg: ITelegramApi, time: TimeProvider, loggerFactory: ILoggerFactory) =
    let plain = PlainRenderer(tg, loggerFactory.CreateLogger<PlainRenderer>()) :> IReplyRenderer
    let editThrottle = EditThrottleRenderer(tg, time, loggerFactory.CreateLogger<EditThrottleRenderer>())
    let draft = DraftRenderer(editThrottle) :> IReplyRenderer

    member _.ForMode(mode: string) : IReplyRenderer =
        match mode with
        | "plain" -> plain
        | "draft" -> draft
        | _ -> editThrottle :> IReplyRenderer
