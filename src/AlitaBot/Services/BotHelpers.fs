module AlitaBot.Services.BotHelpers

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Funogram.Telegram.Types
open AlitaBot
open BotInfra

module Req = Funogram.Telegram.Req

/// Bot user id is the numeric prefix of the bot token ("123456:ABC-..." -> 123456).
/// Shared by every service that logs the bot's own `message_log` rows (BotService,
/// DigestService/Slice 8) — a single source of truth instead of each service parsing
/// `BotToken` itself.
let botUserId (conf: BotConfiguration) =
    match conf.BotToken.Split(':') with
    | [||] -> 0L
    | parts ->
        match Int64.TryParse parts[0] with
        | true, v -> v
        | _ -> 0L

// ── ITelegramApi call wrappers ──────────────────────────────────────────
// All of these throw TelegramApiException on a Telegram API error (CallExn).

/// Sends a text message and returns the sent Message (for callers needing MessageId).
let sendMessage (tg: ITelegramApi) (chatId: int64) (text: string) : Task<Message> =
    tg.CallExn(Req.SendMessage.Make(chatId, text))

/// Sends a text message as a reply to another message (best-effort target:
/// falls back to a plain send server-side when the target is gone).
/// Returns the sent Message — its MessageId is logged to message_log.
let sendTextReply (tg: ITelegramApi) (chatId: int64) (text: string) (replyToMessageId: int64) : Task<Message> =
    let replyParams = ReplyParameters.Create(replyToMessageId, allowSendingWithoutReply = true)
    tg.CallExn(Req.SendMessage.Make(chatId, text, replyParameters = replyParams))

let editMessageText (tg: ITelegramApi) (chatId: int64) (messageId: int64) (text: string) =
    tg.CallExn(Req.EditMessageText.Make(chatId = ChatId.Int chatId, messageId = messageId, text = text)) |> taskIgnore

/// Best-effort delete (e.g. cleaning up the "рисую..." placeholder once the real photo
/// is ready) — fire-and-forget, a failure here must never block the actual reply.
let deleteMessage (tg: ITelegramApi) (chatId: int64) (messageId: int64) =
    tg.CallIgnore(Req.DeleteMessage.Make(chatId, messageId))

/// Sends a photo as a reply, with a caption, via multipart upload (Funogram's
/// InputFile.FileBytes) — used by the /img command to deliver a generated image.
let sendPhotoReply (tg: ITelegramApi) (chatId: int64) (bytes: byte[]) (caption: string) (replyToMessageId: int64) : Task<Message> =
    let replyParams = ReplyParameters.Create(replyToMessageId, allowSendingWithoutReply = true)
    tg.CallExn(Req.SendPhoto.Make(chatId, InputFile.FileBytes("image.png", bytes), caption = caption, replyParameters = replyParams))

/// Sends a text reply carrying explicit entities (e.g. expandable_blockquote for
/// voice transcripts) — entities are mutually exclusive with parse_mode on the wire,
/// so callers that need formatting without a parse_mode use this instead of sendTextReply.
let sendTextReplyWithEntities
    (tg: ITelegramApi)
    (chatId: int64)
    (text: string)
    (entities: MessageEntity[])
    (replyToMessageId: int64)
    : Task<Message> =
    let replyParams = ReplyParameters.Create(replyToMessageId, allowSendingWithoutReply = true)
    tg.CallExn(Req.SendMessage.Make(chatId, text, entities = entities, replyParameters = replyParams))

// ── Ephemeral replies (Bot API 10.2, Phase-1 Slice 4 /summary probe) ───────
//
// Funogram.Telegram 10.2.0's Req.SendMessage carries an optional `receiverUserId`
// (wire: `receiver_user_id`) — passing it makes Telegram deliver the message only to
// that user (an "ephemeral" message per Bot API 10.2), everyone else in the chat never
// sees it. Confirmed by decompiling Funogram.Telegram.dll (ilspycmd -t Funogram.Telegram.Req):
// SendMessage's constructor/Make both carry `FSharpOption<long> receiverUserId`, and the
// response Message carries a matching `FSharpOption<long> EphemeralMessageId`. Whether
// Telegram actually accepts it varies by chat type — see the empirical findings in
// src/AlitaBot/README.md ("Ephemeral message probe").

/// Chats where an ephemeral send has already failed once — permanently (process-lifetime)
/// falls back to a normal reply for the rest of the process, mirroring DraftRenderer's
/// per-chat fallback memo (Services/ReplyRenderer.fs, docs/TECH-DEBT.md) so a chat type
/// that rejects receiver-scoped messages isn't re-probed on every /summary call.
let private ephemeralUnsupportedChats = ConcurrentDictionary<int64, byte>()

/// Sends `text` visible only to `receiverUserId` (Bot API 10.2 ephemeral message) as a
/// reply to `replyToMessageId`. On any Telegram API failure — including a chat type that
/// rejects receiver-scoped messages outright — logs a Warning, remembers the chat
/// (see `ephemeralUnsupportedChats`), and falls back to a normal reply (visible to the
/// whole chat) instead. Once a chat is remembered, later calls skip the ephemeral attempt
/// entirely and go straight to the normal reply.
let trySendEphemeralOrReply
    (tg: ITelegramApi)
    (logger: ILogger)
    (chatId: int64)
    (receiverUserId: int64)
    (text: string)
    (replyToMessageId: int64)
    : Task<Message> =
    task {
        let replyParams = ReplyParameters.Create(replyToMessageId, allowSendingWithoutReply = true)
        if ephemeralUnsupportedChats.ContainsKey chatId then
            return! tg.CallExn(Req.SendMessage.Make(chatId, text, replyParameters = replyParams))
        else
            try
                return!
                    tg.CallExn(
                        Req.SendMessage.Make(chatId, text, receiverUserId = receiverUserId, replyParameters = replyParams))
            with ex ->
                logger.LogWarning(
                    ex,
                    "Ephemeral send rejected for chat {ChatId} — falling back to a normal reply and remembering for the rest of this process",
                    chatId)
                ephemeralUnsupportedChats[chatId] <- 0uy
                return! tg.CallExn(Req.SendMessage.Make(chatId, text, replyParameters = replyParams))
    }

/// Largest PhotoSize of a message's Photo array (Telegram orders smallest -> largest),
/// or None for a message with no photo. Shared by BotService (detecting a photo message)
/// and ResponderService (the vision feature — fetching the image to attach to the LLM request).
let largestPhoto (msg: Message) : PhotoSize option =
    match msg.Photo with
    | Some photos when photos.Length > 0 -> Some(photos |> Array.maxBy (fun p -> p.Width * p.Height))
    | _ -> None
