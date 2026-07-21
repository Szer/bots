namespace AlitaBot.Services

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Funogram.Telegram.Types
open AlitaBot.Llm
open BotInfra

module Req = Funogram.Telegram.Req

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

/// Streams via `sendMessageDraft` (Bot API 10.2): a throttled (≥500ms) draft
/// update per chunk, then ONE real `sendMessage` at the end — Telegram never
/// turns a draft into a real message (it has no message_id, is never editable,
/// and is invisible to history reads; it is wire-identical to a "typing…"
/// indicator that happens to carry the composed text — see
/// TL.SendMessageTextDraftAction / UpdateUserTyping / UpdateChatUserTyping).
///
/// Empirically probed against real Telegram (M5, `make probe-draft`):
///   - Private chats: sendMessageDraft succeeds; the peer sees repeated
///     UpdateUserTyping "drafting…" bubbles, then a normal new message on the
///     final sendMessage. No draft text is ever visible in message history.
///   - Basic groups: sendMessageDraft fails immediately with
///     400 TEXTDRAFT_PEER_INVALID — drafts are not supported there at all.
///     (Supergroups were not reachable from the test harness and remain
///     unverified; TEXTDRAFT_PEER_INVALID strongly suggests the restriction is
///     "not a private chat" rather than "not this specific group", but treat
///     that as an inference, not a probed fact.)
///
/// Because group support is unverified-at-best and confirmed-broken for basic
/// groups — the bot's primary surface — a chat that ever rejects
/// sendMessageDraft is permanently remembered (this process's lifetime) and
/// every subsequent Render for it skips straight to edit-throttle.
type DraftRenderer(tg: ITelegramApi, time: TimeProvider, fallback: EditThrottleRenderer, logger: ILogger<DraftRenderer>) =
    let minDraftInterval = TimeSpan.FromMilliseconds 500.
    let minEditInterval = TimeSpan.FromSeconds 1.5
    let minNewChars = 40

    /// Chats where sendMessageDraft has failed at least once — never retried.
    let unsupportedChats = ConcurrentDictionary<int64, unit>()

    interface IReplyRenderer with
        member _.Render(chatId, replyToMessageId, chunks, ct) =
            if unsupportedChats.ContainsKey chatId then
                (fallback :> IReplyRenderer).Render(chatId, replyToMessageId, chunks, ct)
            else
                task {
                    let draftId = Random.Shared.NextInt64(1L, Int64.MaxValue)
                    let sb = StringBuilder()
                    let mutable outcome = RenderOutcome.Completed FinishReason.Stop

                    // While draftSupported: no real message exists yet, only draft
                    // updates. Once it flips false (draft rejected mid-stream), we
                    // behave exactly like EditThrottleRenderer from that point on.
                    let mutable draftSupported = true
                    let mutable lastDraftLen = 0
                    let mutable lastDraftAt = time.GetTimestamp()

                    let mutable sentMsg: Message option = None
                    let mutable lastSentLen = 0
                    let mutable lastSentAt = time.GetTimestamp()

                    let fallBackToEdit (currentText: string) =
                        task {
                            unsupportedChats[chatId] <- ()
                            draftSupported <- false
                            let! sent = BotHelpers.sendTextReply tg chatId currentText replyToMessageId
                            sentMsg <- Some sent
                            lastSentLen <- currentText.Length
                            lastSentAt <- time.GetTimestamp()
                        }

                    use enumerator = chunks.GetAsyncEnumerator(ct)
                    let mutable go = true
                    while go do
                        let! has = enumerator.MoveNextAsync()
                        if not has then
                            go <- false
                        else
                            match enumerator.Current with
                            | ChatChunk.TextDelta t when t.Length > 0 ->
                                %sb.Append(t)
                                let current = sb.ToString()

                                if draftSupported then
                                    if lastDraftLen = 0 || time.GetElapsedTime(lastDraftAt) >= minDraftInterval then
                                        let req = Req.SendMessageDraft.Make(chatId, draftId, text = current)

                                        match! tg.Call req with
                                        | Ok _ ->
                                            lastDraftLen <- current.Length
                                            lastDraftAt <- time.GetTimestamp()
                                        | Error e ->
                                            logger.LogInformation(
                                                "sendMessageDraft rejected in chat {ChatId} ({Code} {Description}) — falling back to edit-throttle for this chat from now on",
                                                chatId,
                                                e.ErrorCode,
                                                e.Description
                                            )
                                            do! fallBackToEdit current
                                else
                                    match sentMsg with
                                    | None ->
                                        let! sent = BotHelpers.sendTextReply tg chatId current replyToMessageId
                                        sentMsg <- Some sent
                                        lastSentLen <- current.Length
                                        lastSentAt <- time.GetTimestamp()
                                    | Some m ->
                                        if time.GetElapsedTime(lastSentAt) >= minEditInterval
                                           && current.Length - lastSentLen >= minNewChars then
                                            do! BotHelpers.editMessageText tg chatId m.MessageId current
                                            lastSentLen <- current.Length
                                            lastSentAt <- time.GetTimestamp()
                            | ChatChunk.TextDelta _
                            | ChatChunk.ToolCallDelta _ -> ()
                            | ChatChunk.Completed response -> outcome <- RenderOutcome.Completed response.FinishReason
                            | ChatChunk.Failed err -> outcome <- RenderOutcome.Failed err

                    let fullText = sb.ToString()

                    match sentMsg with
                    | Some m ->
                        // Already fell back to a real message mid-stream — finish exactly
                        // like EditThrottleRenderer.
                        match outcome with
                        | RenderOutcome.Failed err ->
                            logger.LogWarning("LLM stream failed after partial text — finalizing partial reply: {Error}", string err)
                        | _ -> ()
                        if fullText.Length <> lastSentLen then
                            do! BotHelpers.editMessageText tg chatId m.MessageId fullText
                        return { FinalMessage = Some m; FullText = fullText; Outcome = outcome }
                    | None ->
                        // Draft-only the whole time (or an empty stream) — drafts never
                        // persist, so the FIRST and ONLY real message is sent right now.
                        match outcome with
                        | RenderOutcome.Failed(LlmError.ContentFiltered _) ->
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

/// Selects a renderer for a STREAM_MODE value ("plain" | "edit" | "draft").
type ReplyRendererFactory(tg: ITelegramApi, time: TimeProvider, loggerFactory: ILoggerFactory) =
    let plain = PlainRenderer(tg, loggerFactory.CreateLogger<PlainRenderer>()) :> IReplyRenderer
    let editThrottle = EditThrottleRenderer(tg, time, loggerFactory.CreateLogger<EditThrottleRenderer>())
    let draft = DraftRenderer(tg, time, editThrottle, loggerFactory.CreateLogger<DraftRenderer>()) :> IReplyRenderer

    member _.ForMode(mode: string) : IReplyRenderer =
        match mode with
        | "plain" -> plain
        | "draft" -> draft
        | _ -> editThrottle :> IReplyRenderer
