namespace AlitaBot.Services

open System
open System.Threading.Tasks
open Dapper
open Npgsql
open AlitaBot.Llm
open BotInfra

[<CLIMutable>]
type MessageLogRow =
    { chat_id: int64
      message_id: int64
      user_id: int64
      username: string | null
      display_name: string
      is_bot: bool
      reply_to_message_id: Nullable<int64>
      text: string
      sent_at: DateTime }

/// One row of `SELECT COUNT(*), SUM(cost_usd)` over `llm_usage` for a time window.
[<CLIMutable>]
type UsageTotalsRow =
    { calls: int64
      cost_usd: decimal }

/// `llm_usage` aggregated by model over a time window (/usage's "по моделям" section).
[<CLIMutable>]
type UsageByModelRow =
    { model: string
      calls: int64
      input_tokens: int64
      output_tokens: int64
      cost_usd: decimal }

/// `llm_usage` aggregated by user over a time window (/usage's top-5 section).
/// `display_name` is resolved from the user's most recent `message_log` row —
/// `llm_usage` itself only stores `user_id` — and is null for a user_id with no
/// matching message_log row (shouldn't normally happen, but the query degrades
/// gracefully rather than dropping the row).
[<CLIMutable>]
type UsageByUserRow =
    { user_id: int64
      display_name: string | null
      calls: int64
      cost_usd: decimal }

/// One row of /ask's semantic-search join (message_embedding + message_log), scored by
/// cosine similarity (1 - cosine distance, pgvector's `<=>` operator).
[<CLIMutable>]
type AskMatchRow =
    { display_name: string
      text: string
      sent_at: DateTime
      similarity: float }

/// F# option -> Dapper-friendly Nullable<'a>, for optional numeric columns.
[<AutoOpen>]
module private DbServiceHelpers =
    let inline toNullable (o: 'a option) : Nullable<'a> =
        match o with
        | Some v -> Nullable v
        | None -> Nullable()

    /// pgvector's text input format: "[v1,v2,...]". G9 round-trips a float32 exactly
    /// without falling back to scientific notation for ordinary embedding magnitudes;
    /// Npgsql sends it as a plain text parameter, cast to `vector` in the SQL itself
    /// (`@embedding::vector`) — avoids taking a dependency on the Pgvector NuGet package
    /// just for parameter binding.
    let vectorLiteral (v: float32[]) : string =
        "[" + (v |> Array.map (fun f -> f.ToString("G9", Globalization.CultureInfo.InvariantCulture)) |> String.concat ",") + "]"

type DbService(connString: string) =
    let openConn() = task {
        let conn = new NpgsqlConnection(connString)
        do! conn.OpenAsync()
        return conn
    }

    /// Idempotent insert: webhook redelivery of the same (chat_id, message_id) is a
    /// no-op. Returns `Some id` when this call actually inserted the row (first
    /// delivery — `id` is the new message_log.id, used to key its message_embedding
    /// row), `None` when it was already there (a duplicate delivery) — callers use this
    /// to skip re-running the LLM/voice/image/embedding work a retry would otherwise
    /// repeat, which the INSERT's own idempotency does not prevent by itself (a webhook
    /// retry would re-run the whole handler and send a second, distinct Telegram reply
    /// even though the log row itself collapses to one). `RETURNING id` naturally
    /// yields nothing when ON CONFLICT DO NOTHING skips the row, so this doubles as the
    /// insert-vs-duplicate signal without a separate existence check.
    member _.LogMessage(row: MessageLogRow) : Task<int64 option> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
INSERT INTO message_log (chat_id, message_id, user_id, username, display_name, is_bot, reply_to_message_id, text, sent_at)
VALUES (@chat_id, @message_id, @user_id, @username, @display_name, @is_bot, @reply_to_message_id, @text, @sent_at)
ON CONFLICT (chat_id, message_id) DO NOTHING
RETURNING id;
"""
            let! id = conn.QuerySingleOrDefaultAsync<Nullable<int64>>(sql, row)
            return if id.HasValue then Some id.Value else None
        }

    /// Inserts one `message_embedding` row (Slice 5a's embedding pipeline). Idempotent
    /// (ON CONFLICT DO NOTHING on the message_log_id PK) so a retried fire-and-forget
    /// embed can never violate the PK. Caller (BotService's embedding pipeline) is
    /// failure-tolerant around this — an exception here is caught by `fireAndForget`,
    /// logged Warning, and never surfaces to the reply path.
    member _.InsertMessageEmbedding(messageLogId: int64, embedding: float32[]) : Task<unit> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
INSERT INTO message_embedding (message_log_id, embedding)
VALUES (@message_log_id, @embedding::vector)
ON CONFLICT (message_log_id) DO NOTHING;
"""
            let! _ =
                conn.ExecuteAsync(
                    sql,
                    {| message_log_id = messageLogId
                       embedding = vectorLiteral embedding |})
            return ()
        }

    /// /ask's semantic search: nearest `topK` message_embedding rows for `chatId` by
    /// cosine distance (pgvector's `<=>`, matching the HNSW index's vector_cosine_ops),
    /// then filtered to `simFloor` and returned oldest-first (citation order). The two-
    /// stage shape (ORDER BY distance LIMIT topK, *then* filter by similarity) lets the
    /// HNSW index serve the LIMIT efficiently while still respecting the floor exactly —
    /// filtering first would force a full scan since pgvector can't push a similarity
    /// WHERE clause into the index.
    member _.SemanticSearch(chatId: int64, queryEmbedding: float32[], topK: int, simFloor: float) : Task<AskMatchRow[]> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT display_name, text, sent_at, similarity FROM (
    SELECT ml.display_name, ml.text, ml.sent_at,
           1 - (me.embedding <=> @qvec::vector) AS similarity
    FROM message_embedding me
    JOIN message_log ml ON ml.id = me.message_log_id
    WHERE ml.chat_id = @chat_id
    ORDER BY me.embedding <=> @qvec::vector
    LIMIT @top_k
) candidates
WHERE similarity >= @sim_floor
ORDER BY sent_at ASC;
"""
            let! rows =
                conn.QueryAsync<AskMatchRow>(
                    sql,
                    {| qvec = vectorLiteral queryEmbedding
                       chat_id = chatId
                       top_k = topK
                       sim_floor = simFloor |})
            return rows |> Seq.toArray
        }

    /// Last `n` messages of the chat, returned in chronological order.
    member _.RecentContext(chatId: int64, n: int) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT chat_id, message_id, user_id, username, display_name, is_bot, reply_to_message_id, text, sent_at
FROM (SELECT * FROM message_log WHERE chat_id = @chat_id ORDER BY sent_at DESC, id DESC LIMIT @n) recent
ORDER BY sent_at ASC, id ASC;
"""
            let! rows = conn.QueryAsync<MessageLogRow>(sql, {| chat_id = chatId; n = n |})
            return rows |> Seq.toArray
        }

    /// Upserts a single bot_setting value (used by /model to publish a new
    /// LLM_DEPLOYMENT) — thin wrapper over BotInfra.DbSettings so callers in
    /// AlitaBot.Services don't need to know the connection string separately.
    member _.UpsertBotSetting(key: string, value: string, typ: string, featureGroup: string) : Task<unit> =
        DbSettings.upsertBotSetting connString key value typ featureGroup

    /// Inserts one `llm_usage` row (Phase-1 Slice 4) — see IUsageRecorder. Called from the
    /// provider telemetry path (LlmCall/ImageCall, LlmTelemetry.fs) on every successful
    /// LLM/STT/TTS/image call; input/output tokens and cost are nullable since not every
    /// call type reports them (e.g. STT's wire response carries no usage block).
    member _.RecordLlmUsage
        (
            kind: string,
            model: string,
            inputTokens: int option,
            outputTokens: int option,
            costUsd: float option,
            chatId: int64 option,
            userId: int64 option
        ) : Task<unit> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
INSERT INTO llm_usage (called_at, kind, model, input_tokens, output_tokens, cost_usd, chat_id, user_id)
VALUES (NOW(), @kind, @model, @input_tokens, @output_tokens, @cost_usd, @chat_id, @user_id);
"""
            let! _ =
                conn.ExecuteAsync(
                    sql,
                    {| kind = kind
                       model = model
                       input_tokens = toNullable inputTokens
                       output_tokens = toNullable outputTokens
                       cost_usd = toNullable (costUsd |> Option.map decimal)
                       chat_id = toNullable chatId
                       user_id = toNullable userId |})
            return ()
        }

    /// Call count + total cost in `llm_usage` since `since` (inclusive) — used for /usage's
    /// "today" and "7 days" headline numbers.
    member _.UsageTotals(since: DateTime) : Task<UsageTotalsRow> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT COUNT(*) AS calls, COALESCE(SUM(cost_usd), 0) AS cost_usd
FROM llm_usage WHERE called_at >= @since;
"""
            return! conn.QuerySingleAsync<UsageTotalsRow>(sql, {| since = since |})
        }

    /// `llm_usage` grouped by model since `since`, most expensive first.
    member _.UsageByModel(since: DateTime) : Task<UsageByModelRow[]> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT model,
       COUNT(*) AS calls,
       COALESCE(SUM(input_tokens), 0) AS input_tokens,
       COALESCE(SUM(output_tokens), 0) AS output_tokens,
       COALESCE(SUM(cost_usd), 0) AS cost_usd
FROM llm_usage
WHERE called_at >= @since
GROUP BY model
ORDER BY cost_usd DESC, calls DESC;
"""
            let! rows = conn.QueryAsync<UsageByModelRow>(sql, {| since = since |})
            return rows |> Seq.toArray
        }

    /// Top `top` users by cost in `llm_usage` since `since` — `display_name` is resolved
    /// from each user's most recent `message_log` row (llm_usage itself has no name).
    member _.UsageByUser(since: DateTime, top: int) : Task<UsageByUserRow[]> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT u.user_id,
       (SELECT ml.display_name FROM message_log ml
        WHERE ml.user_id = u.user_id ORDER BY ml.sent_at DESC LIMIT 1) AS display_name,
       COUNT(*) AS calls,
       COALESCE(SUM(u.cost_usd), 0) AS cost_usd
FROM llm_usage u
WHERE u.called_at >= @since AND u.user_id IS NOT NULL
GROUP BY u.user_id
ORDER BY cost_usd DESC, calls DESC
LIMIT @top;
"""
            let! rows = conn.QueryAsync<UsageByUserRow>(sql, {| since = since; top = top |})
            return rows |> Seq.toArray
        }

    interface IUsageRecorder with
        member this.Record(kind, model, inputTokens, outputTokens, costUsd, ctx: UsageContext) =
            this.RecordLlmUsage(kind, model, inputTokens, outputTokens, costUsd, ctx.ChatId, ctx.UserId)
