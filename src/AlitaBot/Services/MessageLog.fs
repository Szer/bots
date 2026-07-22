namespace AlitaBot.Services

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open AlitaBot
open AlitaBot.Llm
open BotInfra

/// Shared `message_log` bookkeeping — extracted from BotService (S10 PR1 prerequisite) so
/// the NL tool-calling loop (ToolExecutor) can log the bot's own media-tool replies the
/// same way every command/trigger path already does, without a circular dependency on
/// BotService (which now rebinds these as locals: `let logRow = MessageLog.logRow time`,
/// `let logAndEmbed = MessageLog.logAndEmbed logger embeddings db`, keeping every existing
/// call site's arity/shape unchanged).
module MessageLog =
    let logRow
        (time: TimeProvider)
        (chatId: int64)
        (messageId: int64)
        (userId: int64)
        (username: string)
        (displayName: string)
        (isBot: bool)
        (replyTo: int64 option)
        (text: string)
        : MessageLogRow =
        { chat_id = chatId
          message_id = messageId
          user_id = userId
          username = username
          display_name = displayName
          is_bot = isBot
          reply_to_message_id = (match replyTo with Some r -> Nullable r | None -> Nullable())
          text = text
          sent_at = time.GetUtcNow().UtcDateTime }

    /// Skips embedding pure command invocations — bare "/xxx"/"!xxx" text, or the bot's own
    /// "[xxx-cmd] ..." message_log tagging convention — neither carries conversational
    /// content worth indexing for semantic search.
    let isPureCommandText (text: string) =
        let t = text.TrimStart()
        t.StartsWith("/") || t.StartsWith("!") || (t.StartsWith("[") && t.Contains("-cmd]"))

    /// Embeds `text` (batch of 1) and inserts the resulting message_embedding row for
    /// `messageLogId`, fully in the background (BotInfra.Utils.fireAndForget — catches and
    /// Warning-logs any exception on top of the explicit LlmError handling below). No-ops
    /// entirely when EMBED_MESSAGES=false, `text` is shorter than EMBEDDING_MIN_CHARS, or it
    /// looks like a pure command (isPureCommandText).
    let tryEmbed
        (logger: ILogger)
        (embeddings: IEmbeddings)
        (db: DbService)
        (conf: BotConfiguration)
        (chatId: int64)
        (userId: int64)
        (messageLogId: int64)
        (text: string)
        =
        if conf.EmbedMessagesEnabled && text.Length >= conf.EmbeddingMinChars && not (isPureCommandText text) then
            fireAndForget logger "embedding.pipeline" (fun () ->
                task {
                    // An opted-out author's messages are never embedded, mirroring their
                    // exclusion from the nightly dossier job and from recall injection.
                    let! optedOut = db.IsOptedOut(userId)
                    if not optedOut then
                        let ctx: UsageContext = { ChatId = Some chatId; UserId = Some userId }
                        match! embeddings.Embed(conf.EmbeddingDeployment, [ text ], ctx, CancellationToken.None) with
                        | Ok vectors when vectors.Length > 0 && vectors[0].Length > 0 ->
                            do! db.InsertMessageEmbedding(messageLogId, vectors[0])
                        | Ok _ -> ()
                        | Error err ->
                            Metrics.embeddingFailuresTotal.Add(1L)
                            logger.LogWarning("Embedding failed for message_log {Id}: {Error}", messageLogId, string err)
                } :> Task)

    /// Drop-in for `db.LogMessage` at every call site: same `bool` contract (true = first
    /// delivery inserted, false = webhook-redelivery duplicate) every caller relies on, but
    /// additionally kicks off the embedding pipeline (tryEmbed) on a real insert. A
    /// duplicate delivery is never re-embedded.
    let logAndEmbed
        (logger: ILogger)
        (embeddings: IEmbeddings)
        (db: DbService)
        (conf: BotConfiguration)
        (row: MessageLogRow)
        : Task<bool> =
        task {
            match! db.LogMessage(row) with
            | Some id ->
                tryEmbed logger embeddings db conf row.chat_id row.user_id id row.text
                return true
            | None -> return false
        }
