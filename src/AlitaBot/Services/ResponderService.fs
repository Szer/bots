namespace AlitaBot.Services

open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Funogram.Telegram.Types
open AlitaBot
open AlitaBot.Llm
open BotInfra

/// Produces the bot's reply to a triggering message per RESPONDER_MODE:
/// "echo" (M1 walking skeleton) or "llm" (context + streamed chat completion).
type ResponderService(
    tg: ITelegramApi,
    db: DbService,
    chat: IChatCompletion,
    renderers: ReplyRendererFactory,
    options: IOptions<BotConfiguration>,
    logger: ILogger<ResponderService>
) =

    let textTurn (row: MessageLogRow) : ChatMessage =
        if row.is_bot then
            { Role = ChatRole.Assistant
              Content = [ ContentPart.Text row.text ]
              ToolCalls = []
              ToolCallId = None }
        else
            { Role = ChatRole.User
              Content = [ ContentPart.Text $"[{row.display_name}]: {row.text}" ]
              ToolCalls = []
              ToolCallId = None }

    let buildRequest (conf: BotConfiguration) (rows: MessageLogRow[]) (msg: Message) : ChatRequest =
        let system =
            { Role = ChatRole.System
              Content = [ ContentPart.Text conf.SystemPrompt ]
              ToolCalls = []
              ToolCallId = None }
        let context = rows |> Array.map textTurn |> Array.toList
        // The triggering message is logged before dispatch, so it is normally the
        // newest context row already — append it only if the window missed it.
        let current =
            if rows |> Array.exists (fun r -> r.message_id = msg.MessageId) then
                []
            else
                match msg.Text, msg.From with
                | Some text, Some from ->
                    let name =
                        match from.LastName with
                        | Some last -> $"{from.FirstName} {last}"
                        | None -> from.FirstName
                    [ { Role = ChatRole.User
                        Content = [ ContentPart.Text $"[{name}]: {text}" ]
                        ToolCalls = []
                        ToolCallId = None } ]
                | _ -> []
        { Deployment = conf.LlmDeployment
          Messages = system :: context @ current
          Tools = []
          Temperature = None
          MaxTokens = None }

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
            | "llm" ->
                let conf = options.Value
                let! rows = db.RecentContext(msg.Chat.Id, conf.ContextWindowMessages)
                let request = buildRequest conf rows msg
                let chunks = chat.CompleteStream(request, CancellationToken.None)
                let renderer = renderers.ForMode(conf.StreamMode)
                let! result = renderer.Render(msg.Chat.Id, msg.MessageId, chunks, CancellationToken.None)
                match result.FinalMessage with
                | Some sent -> return Some(sent, result.FullText)
                | None -> return None
            | mode ->
                logger.LogWarning("Unknown RESPONDER_MODE '{Mode}' — staying silent", mode)
                return None
        }
