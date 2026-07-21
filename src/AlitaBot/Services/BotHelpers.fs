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
