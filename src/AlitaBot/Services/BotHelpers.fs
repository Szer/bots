module AlitaBot.Services.BotHelpers

open System
open System.Threading.Tasks
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

/// Sends a voice note reply (Bot API `sendVoice`) — used by `/say` when the TTS bytes are
/// already (or were converted to) a proper Ogg/Opus container, which is what makes
/// Telegram clients render it as a playable voice bubble rather than a generic file.
let sendVoiceReply (tg: ITelegramApi) (chatId: int64) (bytes: byte[]) (replyToMessageId: int64) : Task<Message> =
    let replyParams = ReplyParameters.Create(replyToMessageId, allowSendingWithoutReply = true)
    tg.CallExn(Req.SendVoice.Make(chatId, InputFile.FileBytes("voice.ogg", bytes), replyParameters = replyParams))

/// Sends a regular audio attachment reply (Bot API `sendAudio`) — `/say`'s fallback when
/// the TTS bytes aren't a proper Ogg/Opus container and no `ffmpeg` was available to
/// convert them (see BotService.tryConvertToOggOpus): still delivers the audio, just not
/// as a round voice-note bubble.
let sendAudioReply (tg: ITelegramApi) (chatId: int64) (bytes: byte[]) (replyToMessageId: int64) : Task<Message> =
    let replyParams = ReplyParameters.Create(replyToMessageId, allowSendingWithoutReply = true)
    tg.CallExn(Req.SendAudio.Make(chatId, InputFile.FileBytes("voice.mp3", bytes), replyParameters = replyParams))

/// Sends a titled audio attachment reply (Bot API `sendAudio` with `title`/`performer`) —
/// used by `/song` (Gemini/Lyria music generation) so the delivered track shows a name in
/// Telegram's audio player instead of just a bare filename, unlike `/say`'s plain
/// sendAudioReply fallback (a voice-note-style TTS clip has no meaningful "title").
let sendAudioReplyWithTitle
    (tg: ITelegramApi)
    (chatId: int64)
    (bytes: byte[])
    (title: string)
    (replyToMessageId: int64)
    : Task<Message> =
    let replyParams = ReplyParameters.Create(replyToMessageId, allowSendingWithoutReply = true)
    tg.CallExn(
        Req.SendAudio.Make(
            chatId,
            InputFile.FileBytes("song.mp3", bytes),
            title = title,
            performer = "Алита",
            replyParameters = replyParams))

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

// ── Ephemeral replies — RETIRED (Bot API 10.2, Phase-1 Slice 4 /summary probe) ─────
//
// AlitaBot briefly sent /summary replies via Bot API 10.2's ephemeral `sendMessage`
// (`receiver_user_id`, visible only to the requester). Retired after staging feedback
// ("ephemeral messages are not useful") — every /summary reply is now a normal,
// whole-chat-visible `sendTextReply` like every other command. Confirmed by real-Telegram
// probing (see src/AlitaBot/README.md's "Ephemeral message probe [RETIRED]" and
// docs/TECH-DEBT.md) that even when Telegram accepted the ephemeral call, the message was
// never observably delivered to the receiving account — an accepted ephemeral send always
// reported `Message.MessageId = 0`, which is exactly why /summary's replies were invisible
// in staging. `trySendEphemeralOrReply`/`loggableMessageId` (formerly here) and the
// `receiver_user_id` wire field are no longer used anywhere in AlitaBot; the full empirical
// writeup is kept in the docs above rather than deleted.

/// Largest PhotoSize of a message's Photo array (Telegram orders smallest -> largest),
/// or None for a message with no photo. Shared by BotService (detecting a photo message)
/// and ResponderService (the vision feature — fetching the image to attach to the LLM request).
let largestPhoto (msg: Message) : PhotoSize option =
    match msg.Photo with
    | Some photos when photos.Length > 0 -> Some(photos |> Array.maxBy (fun p -> p.Width * p.Height))
    | _ -> None
