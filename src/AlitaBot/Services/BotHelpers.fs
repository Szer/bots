module AlitaBot.Services.BotHelpers

open System.Threading.Tasks
open Funogram.Telegram.Types
open BotInfra

module Req = Funogram.Telegram.Req

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

/// Largest PhotoSize of a message's Photo array (Telegram orders smallest -> largest),
/// or None for a message with no photo. Shared by BotService (detecting a photo message)
/// and ResponderService (the vision feature — fetching the image to attach to the LLM request).
let largestPhoto (msg: Message) : PhotoSize option =
    match msg.Photo with
    | Some photos when photos.Length > 0 -> Some(photos |> Array.maxBy (fun p -> p.Width * p.Height))
    | _ -> None
