namespace AlitaBot.Services

open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Funogram.Telegram.Types
open AlitaBot
open BotInfra

/// Produces the bot's reply to a triggering message. M1 ships the "echo" mode only;
/// an IChatCompletion-backed "llm" mode slots into the match at M2.
type ResponderService(tg: ITelegramApi, options: IOptions<BotConfiguration>, logger: ILogger<ResponderService>) =

    /// Replies to `msg` per the configured RESPONDER_MODE.
    /// Returns the sent Message and the reply text, or None when no reply was produced.
    member _.Respond(msg: Message) : Task<(Message * string) option> =
        task {
            match options.Value.ResponderMode with
            | "echo" ->
                let original = msg.Text |> Option.defaultValue ""
                let replyText = $"pong: {original}"
                let! sent = BotHelpers.sendTextReply tg msg.Chat.Id replyText msg.MessageId
                return Some(sent, replyText)
            | mode ->
                logger.LogWarning("Unknown RESPONDER_MODE '{Mode}' — staying silent", mode)
                return None
        }
