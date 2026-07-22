namespace AlitaBot.Services

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Funogram.Telegram.Types
open AlitaBot
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

/// MarkdownV2-formats a renderer's FINAL delivered message (Slice 6, plan §4) — applied
/// once, at the very end of a reply, never to the plain-text partials streamed along the
/// way. Kept separate from `BotHelpers` because command outputs (`/usage` etc.) explicitly
/// keep their current plain-text formatting path — only the three `IReplyRenderer`s below
/// call into this.
module Mdv2Delivery =
    [<Literal>]
    let TelegramMaxLen = 4096

    /// Chats where `Req.SendRichMessage` has failed at least once — memoized permanently
    /// (process-lifetime), mirroring `DraftRenderer`'s per-chat fallback memo: a chat that
    /// rejects it once always gets plain multipart sends for an over-length final reply
    /// from then on, instead of re-probing every time.
    let private richMessageUnsupportedChats = ConcurrentDictionary<int64, unit>()

    /// Splits `text` into chunks of at most `TelegramMaxLen` chars, preferring to break on
    /// a newline in the back half of the window so a long reply doesn't get chopped
    /// mid-sentence when a nearby line break is available.
    let splitPlain (text: string) : string list =
        if text.Length <= TelegramMaxLen then
            [ text ]
        else
            let rec go (remaining: string) acc =
                if remaining.Length <= TelegramMaxLen then
                    List.rev (remaining :: acc)
                else
                    let window = remaining.Substring(0, TelegramMaxLen)
                    let nl = window.LastIndexOf '\n'
                    let breakAt = if nl > TelegramMaxLen / 2 then nl + 1 else TelegramMaxLen
                    go (remaining.Substring(breakAt)) (remaining.Substring(0, breakAt) :: acc)
            go text []

    let private sendPlainMultipart
        (tg: ITelegramApi)
        (chatId: int64)
        (replyToMessageId: int64)
        (fullText: string)
        : Task<Message> =
        task {
            let mutable last = Unchecked.defaultof<Message>
            for chunk in splitPlain fullText do
                let! sent = BotHelpers.sendTextReply tg chatId chunk replyToMessageId
                last <- sent
            return last
        }

    /// Same chunking as `sendPlainMultipart`, but as fresh (non-reply) messages —
    /// `sendFinalToChat`'s over-length/rejected-rich-message fallback.
    let private sendPlainMultipartToChat (tg: ITelegramApi) (chatId: int64) (fullText: string) : Task<Message> =
        task {
            let mutable last = Unchecked.defaultof<Message>
            for chunk in splitPlain fullText do
                let! sent = BotHelpers.sendMessage tg chatId chunk
                last <- sent
            return last
        }

    /// Sends the FINAL reply for a response that hasn't sent anything yet (`PlainRenderer`,
    /// and the streaming renderers' edge cases that skip straight to one send). MDV2-
    /// formats `fullText`; on a Telegram 400 (bad entities) falls back to a plain-text
    /// resend (Warning-logged, `alitabot_mdv2_fallback_total`). When the MDV2 form exceeds
    /// Telegram's 4096-char message limit, escalates to `Req.SendRichMessage` (markdown
    /// variant) instead of a formatted `sendMessage`; a chat that rejects THAT falls back
    /// to plain multipart sends, memoized per chat from then on (probe-and-fallback,
    /// mirroring `DraftRenderer`).
    let sendFinal
        (tg: ITelegramApi)
        (logger: ILogger)
        (chatId: int64)
        (replyToMessageId: int64)
        (fullText: string)
        : Task<Message> =
        task {
            let mdv2 = MarkdownRenderer.toMarkdownV2 fullText
            let replyParams = ReplyParameters.Create(replyToMessageId, allowSendingWithoutReply = true)
            if mdv2.Length <= TelegramMaxLen then
                try
                    return!
                        tg.CallExn(
                            Req.SendMessage.Make(chatId, mdv2, parseMode = ParseMode.MarkdownV2, replyParameters = replyParams))
                with ex ->
                    Metrics.mdv2FallbackTotal.Add(1L)
                    logger.LogWarning(ex, "MDV2 sendMessage rejected by Telegram — falling back to plain text")
                    return! BotHelpers.sendTextReply tg chatId fullText replyToMessageId
            elif richMessageUnsupportedChats.ContainsKey chatId then
                return! sendPlainMultipart tg chatId replyToMessageId fullText
            else
                try
                    let rich = InputRichMessage.Create(markdown = mdv2)
                    return! tg.CallExn(Req.SendRichMessage.Make(chatId, rich, replyParameters = replyParams))
                with ex ->
                    richMessageUnsupportedChats[chatId] <- ()
                    logger.LogWarning(
                        ex,
                        "SendRichMessage rejected by Telegram — falling back to plain multipart sends for chat {ChatId}",
                        chatId)
                    return! sendPlainMultipart tg chatId replyToMessageId fullText
        }

    /// Slice 8: sends `fullText` as a fresh (non-reply) message to `chatId` — the morning
    /// digest has no triggering message to reply to. Same MDV2-with-fallback policy as
    /// `sendFinal`, just without `replyParameters` anywhere on the wire.
    let sendFinalToChat (tg: ITelegramApi) (logger: ILogger) (chatId: int64) (fullText: string) : Task<Message> =
        task {
            let mdv2 = MarkdownRenderer.toMarkdownV2 fullText
            if mdv2.Length <= TelegramMaxLen then
                try
                    return! tg.CallExn(Req.SendMessage.Make(chatId, mdv2, parseMode = ParseMode.MarkdownV2))
                with ex ->
                    Metrics.mdv2FallbackTotal.Add(1L)
                    logger.LogWarning(ex, "MDV2 sendMessage rejected by Telegram — falling back to plain text")
                    return! BotHelpers.sendMessage tg chatId fullText
            elif richMessageUnsupportedChats.ContainsKey chatId then
                return! sendPlainMultipartToChat tg chatId fullText
            else
                try
                    let rich = InputRichMessage.Create(markdown = mdv2)
                    return! tg.CallExn(Req.SendRichMessage.Make(chatId, rich))
                with ex ->
                    richMessageUnsupportedChats[chatId] <- ()
                    logger.LogWarning(
                        ex,
                        "SendRichMessage rejected by Telegram — falling back to plain multipart sends for chat {ChatId}",
                        chatId)
                    return! sendPlainMultipartToChat tg chatId fullText
        }

    /// Finalizes a streaming renderer's already-sent message with MDV2 formatting — the
    /// interim edits along the way stayed plain text; this is the one edit that switches
    /// the message over to its formatted form. On a Telegram 400 falls back to a plain-
    /// text edit (Warning-logged, `alitabot_mdv2_fallback_total`). A final text whose MDV2
    /// form exceeds 4096 chars skips MDV2 entirely and edits plain — an edit can't
    /// escalate into multiple messages the way a fresh send can (`sendFinal` above); this
    /// case is rare in practice since the plain text would already have needed >4096 chars
    /// to stream that far without Telegram rejecting the interim edit first.
    let editFinal (tg: ITelegramApi) (logger: ILogger) (chatId: int64) (messageId: int64) (fullText: string) : Task<unit> =
        task {
            let mdv2 = MarkdownRenderer.toMarkdownV2 fullText
            if mdv2.Length > TelegramMaxLen then
                do! BotHelpers.editMessageText tg chatId messageId fullText
            else
                try
                    do!
                        tg.CallExn(
                            Req.EditMessageText.Make(
                                chatId = ChatId.Int chatId,
                                messageId = messageId,
                                text = mdv2,
                                parseMode = ParseMode.MarkdownV2))
                        |> taskIgnore
                with ex ->
                    Metrics.mdv2FallbackTotal.Add(1L)
                    logger.LogWarning(ex, "MDV2 editMessageText rejected by Telegram — falling back to a plain edit")
                    do! BotHelpers.editMessageText tg chatId messageId fullText
        }

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
                    let! sent = Mdv2Delivery.sendFinal tg logger chatId replyToMessageId fullText
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
                    // Always fires, even when fullText already equals what was last
                    // streamed plain — the streamed partials were deliberately plain text
                    // (Mdv2Delivery applies only at the FINAL message), so this edit is
                    // what actually switches the message over to its MDV2-formatted form.
                    do! Mdv2Delivery.editFinal tg logger chatId m.MessageId fullText
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
                        // like EditThrottleRenderer (always a final MDV2 edit, see there).
                        match outcome with
                        | RenderOutcome.Failed err ->
                            logger.LogWarning("LLM stream failed after partial text — finalizing partial reply: {Error}", string err)
                        | _ -> ()
                        do! Mdv2Delivery.editFinal tg logger chatId m.MessageId fullText
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
                            let! sent = Mdv2Delivery.sendFinal tg logger chatId replyToMessageId fullText
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
