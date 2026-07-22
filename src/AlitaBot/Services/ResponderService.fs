namespace AlitaBot.Services

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Funogram.Telegram.Types
open AlitaBot
open AlitaBot.Llm
open BotInfra

module Req = Funogram.Telegram.Req

/// Produces the bot's reply to a triggering message per RESPONDER_MODE:
/// "echo" (M1 walking skeleton) or "llm" (context + streamed chat completion).
type ResponderService(
    tg: ITelegramApi,
    db: DbService,
    chat: IChatCompletion,
    embeddings: IEmbeddings,
    renderers: ReplyRendererFactory,
    options: IOptions<BotConfiguration>,
    logger: ILogger<ResponderService>
) =

    /// Vision feature cap (plan §2): current message + its reply target, never more.
    [<Literal>]
    let VisionMaxImages = 2

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

    /// Candidate messages to pull an image from, in priority order: the triggering
    /// message itself, then the message it replies to — capped at VisionMaxImages and
    /// filtered to ones that actually carry a photo. The reply-to candidate depends on
    /// Telegram having attached the full replied-to Message object to THIS update
    /// (guaranteed for a direct reply; if the chain goes deeper, or the source message
    /// is otherwise unavailable, ReplyToMessage is simply absent) — when it's missing
    /// there is no file_id to re-fetch, so that image is silently skipped.
    let imageCandidates (msg: Message) : Message list =
        [ msg; yield! (msg.ReplyToMessage |> Option.toList) ]
        |> List.filter (fun m -> (BotHelpers.largestPhoto m).IsSome)
        |> List.truncate VisionMaxImages

    /// Downloads one candidate's largest photo and re-encodes it as a base64 data: URL
    /// (Telegram photo sizes are always JPEG). Best-effort: any failure (API error,
    /// network error, missing FilePath) logs a Warning and yields None so the caller
    /// degrades to a text-only request instead of failing the whole reply.
    let tryFetchImagePart (conf: BotConfiguration) (m: Message) : Task<ContentPart option> =
        task {
            match BotHelpers.largestPhoto m with
            | None -> return None
            | Some photo ->
                try
                    let! file = tg.CallExn(Req.GetFile.Make(photo.FileId))
                    match file.FilePath with
                    | None -> return None
                    | Some fp when String.IsNullOrWhiteSpace fp -> return None
                    | Some fp ->
                        let! bytes = tg.DownloadFile fp
                        let url = $"data:image/jpeg;base64,{Convert.ToBase64String bytes}"
                        return Some(ContentPart.ImageUrl(url, Some conf.VisionDetail))
                with ex ->
                    logger.LogWarning(
                        ex,
                        "Vision: failed to fetch photo {FileId} for message {MessageId} — skipping image, proceeding text-only",
                        photo.FileId,
                        m.MessageId)
                    return None
        }

    /// All image parts to attach to the triggering user's turn (0-2), respecting VISION_ENABLED.
    let collectImageParts (conf: BotConfiguration) (msg: Message) : Task<ContentPart list> =
        task {
            if not conf.VisionEnabled then
                return []
            else
                let! parts = imageCandidates msg |> List.map (tryFetchImagePart conf) |> Task.WhenAll
                return parts |> Array.choose id |> Array.toList
        }

    /// Slice 5b recall injection: when DOSSIER_ENABLED and the triggering message's author
    /// has a dossier (and isn't opted out), returns the text to append to the system
    /// prompt — "Досье автора:\n{summary}" plus up to DOSSIER_RECALL_K ACTIVE
    /// interaction_memory facts for that author, scored by cosine similarity against the
    /// incoming message's own text (floor DOSSIER_SIM_FLOOR) — never an unfiltered "just
    /// the nearest K" (see the V4 migration's header comment on why /ask and this both
    /// apply a floor). Returns "" for everything else (feature off, no author, no
    /// dossier, opted out, embed failure, or no facts above the floor) — additive-only,
    /// never affects the rest of the request build.
    let dossierContextFor (conf: BotConfiguration) (msg: Message) : Task<string> =
        task {
            if not conf.DossierEnabled then
                return ""
            else
                match msg.From with
                | None -> return ""
                | Some author ->
                    let! optedOut = db.IsOptedOut(author.Id)
                    if optedOut then
                        return ""
                    else
                        match! db.GetPersonDossier(author.Id) with
                        | None -> return ""
                        | Some dossier ->
                            let messageText = msg.Text |> Option.orElse msg.Caption |> Option.defaultValue ""
                            let! factsText =
                                task {
                                    if String.IsNullOrWhiteSpace messageText then
                                        return ""
                                    else
                                        let ctx: UsageContext = { ChatId = Some msg.Chat.Id; UserId = Some author.Id }
                                        match!
                                            embeddings.Embed(conf.EmbeddingDeployment, [ messageText ], ctx, CancellationToken.None)
                                        with
                                        | Ok vectors when vectors.Length > 0 && vectors[0].Length > 0 ->
                                            let! facts =
                                                db.NearestActiveFacts(author.Id, vectors[0], conf.DossierRecallK, conf.DossierSimFloor)
                                            if facts.Length = 0 then
                                                return ""
                                            else
                                                return
                                                    "\n\nИзвестные факты об авторе:\n"
                                                    + (facts |> Array.map (fun f -> $"- {f.content}") |> String.concat "\n")
                                        | Ok _ -> return ""
                                        | Error err ->
                                            logger.LogWarning(
                                                "Dossier recall: failed to embed the triggering message: {Error}",
                                                string err)
                                            return ""
                                }
                            return $"\n\nДосье автора:\n{dossier.summary}{factsText}"
        }

    let buildRequest (conf: BotConfiguration) (rows: MessageLogRow[]) (msg: Message) : Task<ChatRequest> =
        task {
            let! dossierContext = dossierContextFor conf msg
            let system =
                { Role = ChatRole.System
                  Content = [ ContentPart.Text(conf.SystemPrompt + dossierContext) ]
                  ToolCalls = []
                  ToolCallId = None }
            let context = rows |> Array.map textTurn |> Array.toList
            // The triggering message is logged before dispatch, so it is normally the
            // newest context row already — its index tells us which ChatMessage to
            // attach image parts to (images are never derivable from message_log text
            // alone; they're fetched fresh from `msg` below).
            let currentIdx = rows |> Array.tryFindIndex (fun r -> r.message_id = msg.MessageId)

            let! imageParts = collectImageParts conf msg

            let contextWithImages =
                match currentIdx, imageParts with
                | Some i, (_ :: _) ->
                    context
                    |> List.mapi (fun j cm -> if j = i then { cm with Content = cm.Content @ imageParts } else cm)
                | _ -> context

            // Only reached when the context window missed the just-logged row (tiny
            // CONTEXT_WINDOW_MESSAGES) — falls back to Caption too, since a photo
            // message's Text is always None.
            let current =
                match currentIdx with
                | Some _ -> []
                | None ->
                    match msg.Text |> Option.orElse msg.Caption, msg.From with
                    | Some text, Some from ->
                        let name =
                            match from.LastName with
                            | Some last -> $"{from.FirstName} {last}"
                            | None -> from.FirstName
                        [ { Role = ChatRole.User
                            Content = ContentPart.Text $"[{name}]: {text}" :: imageParts
                            ToolCalls = []
                            ToolCallId = None } ]
                    | _ -> []

            return
                { Deployment = conf.LlmDeployment
                  Messages = system :: contextWithImages @ current
                  Tools = []
                  Temperature = None
                  MaxTokens = None }
        }

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
                let! request = buildRequest conf rows msg
                let ctx: UsageContext = { ChatId = Some msg.Chat.Id; UserId = msg.From |> Option.map (fun u -> u.Id) }
                let chunks = chat.CompleteStream(request, ctx, CancellationToken.None)
                let renderer = renderers.ForMode(conf.StreamMode)
                let! result = renderer.Render(msg.Chat.Id, msg.MessageId, chunks, CancellationToken.None)
                match result.FinalMessage with
                | Some sent -> return Some(sent, result.FullText)
                | None -> return None
            | mode ->
                logger.LogWarning("Unknown RESPONDER_MODE '{Mode}' — staying silent", mode)
                return None
        }
