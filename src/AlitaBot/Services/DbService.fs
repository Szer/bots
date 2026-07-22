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

/// One `person_dossier` row (Slice 5b).
[<CLIMutable>]
type PersonDossierRow =
    { user_id: int64
      display_name: string | null
      summary: string
      updated_at: Nullable<DateTime> }

/// One `interaction_memory` row as rendered by `/dossier` (newest-first, no similarity —
/// unlike ResponderService's recall, `/dossier` isn't scored against a query).
[<CLIMutable>]
type ActiveFactRow = { content: string; created_at: DateTime }

/// One `interaction_memory` row recalled by cosine similarity (ResponderService) — same
/// shape as AskMatchRow's `similarity` convention (1 - cosine distance).
[<CLIMutable>]
type RecalledFactRow = { content: string; similarity: float }

/// One `message_log` row resolved by username (Slice 7 /roast target resolution) — the
/// user_id plus their most recently seen display_name, in one round trip.
[<CLIMutable>]
type ResolvedUserRow = { user_id: int64; display_name: string }

/// Burst-activity stats for one chat over a lookback window (Slice 8 interjection gate)
/// — count of non-bot `message_log` rows and distinct authors among them.
[<CLIMutable>]
type BurstStatsRow = { message_count: int64; distinct_users: int64 }

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

/// Safety guard for `/sql`'s LLM-generated SQL (Slice 9 stretch) — a simple, deliberately
/// conservative text-level check applied BEFORE the query ever reaches Postgres. This is
/// belt-and-braces on top of the real safety net (DbService.ExecuteReadOnlySelect opens a
/// connection with `SET default_transaction_read_only = on` and wraps every query in an
/// outer `SELECT ... LIMIT 50`) — even a query that somehow slipped past this guard could
/// still only SELECT, and only up to 50 rows.
module SqlGuard =
    /// Keywords that would attempt a write/DDL operation — checked as whole words, case-
    /// insensitively, against the SQL text with any single-quoted string literals blanked
    /// out first (so a keyword appearing only inside a quoted string, e.g. a WHERE clause
    /// literal, is never mistaken for an attempted statement).
    let private dangerousKeywords = [ "INSERT"; "UPDATE"; "DELETE"; "DROP"; "ALTER"; "CREATE"; "GRANT" ]

    /// Blanks out the contents of every single-quoted string literal (handling ''-escaped
    /// quotes) so keyword/semicolon checks never fire on quoted text — e.g. a query whose
    /// WHERE clause legitimately contains the word "update" as a literal string value.
    let private stripStringLiterals (sql: string) : string =
        let sb = Text.StringBuilder(sql.Length)
        let mutable i = 0
        let mutable inStr = false
        while i < sql.Length do
            let c = sql[i]
            if inStr then
                if c = '\'' then
                    if i + 1 < sql.Length && sql[i + 1] = '\'' then
                        i <- i + 1 // escaped '' — consume both, emit nothing
                    else
                        inStr <- false
            elif c = '\'' then
                inStr <- true
            else
                %sb.Append(c)
            i <- i + 1
        sb.ToString()

    /// Validates a model-generated SQL string: must be a single SELECT/WITH statement (at
    /// most one trailing semicolon, none elsewhere) with none of `dangerousKeywords`
    /// appearing outside a quoted string literal. On success returns the statement with
    /// its trailing semicolon (if any) stripped — ready to be wrapped in an outer
    /// `SELECT ... LIMIT`. On failure returns a short RU reason suitable for the reply.
    let validate (sql: string) : Result<string, string> =
        let trimmed = sql.Trim()
        if trimmed = "" then
            Error "пустой запрос"
        else
            let withoutTrailingSemi =
                if trimmed.EndsWith(";") then trimmed.Substring(0, trimmed.Length - 1).TrimEnd() else trimmed
            let stripped = stripStringLiterals withoutTrailingSemi
            let upper = stripped.TrimStart().ToUpperInvariant()
            if not (upper.StartsWith "SELECT" || upper.StartsWith "WITH") then
                Error "запрос должен начинаться с SELECT или WITH"
            elif stripped.Contains ";" then
                Error "разрешён только один SQL-оператор"
            else
                let upperStripped = stripped.ToUpperInvariant()
                let hits =
                    dangerousKeywords
                    |> List.filter (fun kw -> Text.RegularExpressions.Regex.IsMatch(upperStripped, $@"\b{kw}\b"))
                if not hits.IsEmpty then
                    Error $"""запрещённые операции: {String.concat ", " hits}"""
                else
                    Ok withoutTrailingSemi

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

    // ── Dossiers (Slice 5b: per-person dossiers + nightly fact extraction) ─────

    /// True when `userId` has opted out of memory (`/forget-me`) — checked by the inline
    /// embedding pipeline (BotService.tryEmbed, skips embedding for an opted-out author),
    /// the nightly job's active-user query (ActiveUsersLast24h, excludes opted-out users
    /// outright), and ResponderService's recall injection (skips a dossier lookup entirely).
    member _.IsOptedOut(userId: int64) : Task<bool> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql = "SELECT EXISTS(SELECT 1 FROM memory_opt_out WHERE user_id = @user_id);"
            return! conn.QuerySingleAsync<bool>(sql, {| user_id = userId |})
        }

    /// Distinct `user_id`s with at least one non-bot `message_log` row since `since`,
    /// excluding opted-out users — the nightly job's candidate set.
    member _.ActiveUsersLast24h(since: DateTime) : Task<int64[]> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT DISTINCT ml.user_id
FROM message_log ml
WHERE ml.is_bot = FALSE
  AND ml.sent_at >= @since
  AND NOT EXISTS (SELECT 1 FROM memory_opt_out mo WHERE mo.user_id = ml.user_id);
"""
            let! rows = conn.QueryAsync<int64>(sql, {| since = since |})
            return rows |> Seq.toArray
        }

    /// `userId`'s own (non-bot) messages since `since`, chronological order — the nightly
    /// job's fact-extraction input. Deliberately scoped to the user's own words only
    /// (never what other people said about them) to avoid extracting facts about someone
    /// from a message they didn't write.
    member _.UserMessagesLast24h(userId: int64, since: DateTime) : Task<MessageLogRow[]> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT chat_id, message_id, user_id, username, display_name, is_bot, reply_to_message_id, text, sent_at
FROM message_log
WHERE user_id = @user_id AND is_bot = FALSE AND sent_at >= @since
ORDER BY sent_at ASC, id ASC;
"""
            let! rows = conn.QueryAsync<MessageLogRow>(sql, {| user_id = userId; since = since |})
            return rows |> Seq.toArray
        }

    /// Highest cosine similarity between `embedding` and `userId`'s ACTIVE
    /// (`valid_to IS NULL`) interaction_memory facts, or `None` when they have none yet —
    /// the nightly job's dedup check (DossierService skips inserting a candidate fact
    /// when this is >= its dedup floor).
    member _.NearestActiveFactSimilarity(userId: int64, embedding: float32[]) : Task<float option> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT 1 - (embedding <=> @qvec::vector) AS similarity
FROM interaction_memory
WHERE user_id = @user_id AND valid_to IS NULL
ORDER BY embedding <=> @qvec::vector
LIMIT 1;
"""
            let! sim =
                conn.QuerySingleOrDefaultAsync<Nullable<float>>(
                    sql,
                    {| user_id = userId; qvec = vectorLiteral embedding |})
            return if sim.HasValue then Some sim.Value else None
        }

    /// Inserts one new active `interaction_memory` fact for `userId` (nightly job, after
    /// NearestActiveFactSimilarity found no near-duplicate).
    member _.InsertInteractionMemory(userId: int64, content: string, embedding: float32[]) : Task<unit> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
INSERT INTO interaction_memory (user_id, content, embedding)
VALUES (@user_id, @content, @embedding::vector);
"""
            let! _ =
                conn.ExecuteAsync(
                    sql,
                    {| user_id = userId
                       content = content
                       embedding = vectorLiteral embedding |})
            return ()
        }

    /// ResponderService's recall: the `topK` nearest ACTIVE interaction_memory facts for
    /// `userId` by cosine distance (HNSW-served), filtered to `simFloor` — same two-stage
    /// shape as SemanticSearch (index serves the LIMIT, the floor is applied after).
    member _.NearestActiveFacts(userId: int64, queryEmbedding: float32[], topK: int, simFloor: float) : Task<RecalledFactRow[]> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT content, similarity FROM (
    SELECT content, 1 - (embedding <=> @qvec::vector) AS similarity
    FROM interaction_memory
    WHERE user_id = @user_id AND valid_to IS NULL
    ORDER BY embedding <=> @qvec::vector
    LIMIT @top_k
) candidates
WHERE similarity >= @sim_floor
ORDER BY similarity DESC;
"""
            let! rows =
                conn.QueryAsync<RecalledFactRow>(
                    sql,
                    {| qvec = vectorLiteral queryEmbedding
                       user_id = userId
                       top_k = topK
                       sim_floor = simFloor |})
            return rows |> Seq.toArray
        }

    /// `/dossier`'s newest-`limit` active facts for `userId`, newest first.
    member _.NewestActiveFacts(userId: int64, limit: int) : Task<ActiveFactRow[]> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT content, created_at FROM interaction_memory
WHERE user_id = @user_id AND valid_to IS NULL
ORDER BY created_at DESC
LIMIT @limit;
"""
            let! rows = conn.QueryAsync<ActiveFactRow>(sql, {| user_id = userId; limit = limit |})
            return rows |> Seq.toArray
        }

    /// `userId`'s dossier row, or `None` when they don't have one yet ("пусто, я тебя ещё
    /// не изучила" — `/dossier` and ResponderService's recall both treat this the same way).
    member _.GetPersonDossier(userId: int64) : Task<PersonDossierRow option> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT user_id, display_name, summary, updated_at
FROM person_dossier WHERE user_id = @user_id;
"""
            let! row = conn.QuerySingleOrDefaultAsync<PersonDossierRow>(sql, {| user_id = userId |})
            return if box row = null then None else Some row
        }

    /// Upserts `userId`'s cumulative dossier summary (nightly job, after a summary-merge
    /// LLM call produced new text).
    member _.UpsertPersonDossier(userId: int64, displayName: string, summary: string) : Task<unit> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
INSERT INTO person_dossier (user_id, display_name, summary, updated_at)
VALUES (@user_id, @display_name, @summary, NOW())
ON CONFLICT (user_id) DO UPDATE
SET display_name = EXCLUDED.display_name, summary = EXCLUDED.summary, updated_at = EXCLUDED.updated_at;
"""
            let! _ = conn.ExecuteAsync(sql, {| user_id = userId; display_name = displayName; summary = summary |})
            return ()
        }

    /// Resolves a `@username` (no leading `@`) to the most recently seen `user_id` for it
    /// in `message_log` — `/dossier @username`'s lookup. `None` when nobody with that
    /// username has ever been logged.
    member _.ResolveUserIdByUsername(username: string) : Task<int64 option> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT user_id FROM message_log
WHERE username = @username
ORDER BY sent_at DESC LIMIT 1;
"""
            let! id = conn.QuerySingleOrDefaultAsync<Nullable<int64>>(sql, {| username = username |})
            return if id.HasValue then Some id.Value else None
        }

    /// `/forget-me` step 1: records the opt-out (idempotent — a repeat call just refreshes
    /// nothing, ON CONFLICT DO NOTHING keeps the original opt-out timestamp).
    member _.OptOutUser(userId: int64) : Task<unit> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
INSERT INTO memory_opt_out (user_id) VALUES (@user_id)
ON CONFLICT (user_id) DO NOTHING;
"""
            let! _ = conn.ExecuteAsync(sql, {| user_id = userId |})
            return ()
        }

    /// `/forget-me` step 2: hard-deletes everything memory-related for `userId` —
    /// interaction_memory facts, the dossier summary, and their message_embedding rows
    /// (joined via message_log, since message_embedding itself has no user_id column).
    /// `message_log` rows are deliberately left alone — the shared chat record, not
    /// personal memory (see the V4 migration's header comment).
    member _.PurgeUserMemory(userId: int64) : Task<unit> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
DELETE FROM interaction_memory WHERE user_id = @user_id;
DELETE FROM person_dossier WHERE user_id = @user_id;
DELETE FROM message_embedding
WHERE message_log_id IN (SELECT id FROM message_log WHERE user_id = @user_id);
"""
            let! _ = conn.ExecuteAsync(sql, {| user_id = userId |})
            return ()
        }

    // ── Social engine (Slice 7: /roast, /awards, /quote, karma) ────────────────

    /// Resolves a `@username` (no leading `@`) to the most recently seen `(user_id,
    /// display_name)` for it in `message_log` — `/roast`'s target resolution. `None` when
    /// nobody with that username has ever been logged. Same lookup shape as
    /// `ResolveUserIdByUsername`, just also returning the display_name /roast needs to
    /// personalize the LLM prompt without a second round trip.
    member _.ResolveUserByUsername(username: string) : Task<(int64 * string) option> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT user_id, display_name FROM message_log
WHERE username = @username
ORDER BY sent_at DESC LIMIT 1;
"""
            let! row = conn.QuerySingleOrDefaultAsync<ResolvedUserRow>(sql, {| username = username |})
            return if box row = null then None else Some(row.user_id, row.display_name)
        }

    /// `userId`'s last `limit` own (non-bot) messages, all-time, chronological order — the
    /// `/roast` ammunition's "their own quotes" ingredient (K=8 dossier facts get their
    /// newness from `NewestActiveFacts`; this is the analogous "newest N raw quotes" pull,
    /// deliberately not time-boxed to 24h the way the nightly job's extraction input is).
    member _.UserRecentMessages(userId: int64, limit: int) : Task<MessageLogRow[]> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT chat_id, message_id, user_id, username, display_name, is_bot, reply_to_message_id, text, sent_at
FROM (SELECT * FROM message_log WHERE user_id = @user_id AND is_bot = FALSE ORDER BY sent_at DESC, id DESC LIMIT @limit) recent
ORDER BY sent_at ASC, id ASC;
"""
            let! rows = conn.QueryAsync<MessageLogRow>(sql, {| user_id = userId; limit = limit |})
            return rows |> Seq.toArray
        }

    /// This chat's human (non-bot) message_log rows since `since`, excluding pure command
    /// invocations (`/xxx`, `!xxx`, or the `"[xxx-cmd] ..."` tagging convention — same
    /// pattern as `BotService.isPureCommandText`), capped at `limit` and chronological.
    /// Shared by `/awards` (7-day window, ~800 cap) and `/quote` (24h window) — neither
    /// wants the bot's own replies or command noise polluting the transcript the awards/
    /// quote-pick LLM call reasons over.
    member _.HumanMessagesSince(chatId: int64, since: DateTime, limit: int) : Task<MessageLogRow[]> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT chat_id, message_id, user_id, username, display_name, is_bot, reply_to_message_id, text, sent_at FROM (
    SELECT * FROM message_log
    WHERE chat_id = @chat_id AND is_bot = FALSE AND sent_at >= @since
      AND text !~ '^(/|!|\[[a-zA-Z0-9_-]+-cmd\])'
    ORDER BY sent_at DESC, id DESC
    LIMIT @limit
) recent
ORDER BY sent_at ASC, id ASC;
"""
            let! rows = conn.QueryAsync<MessageLogRow>(sql, {| chat_id = chatId; since = since; limit = limit |})
            return rows |> Seq.toArray
        }

    /// Most recent `/roast` timestamp for `targetUserId`, or `None` if they've never been
    /// roasted — `ROAST_COOLDOWN_SECONDS`'s cooldown check.
    member _.LastRoastedAt(targetUserId: int64) : Task<DateTime option> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql = "SELECT last_roasted_at FROM roast_cooldown WHERE target_user_id = @target_user_id;"
            let! at = conn.QuerySingleOrDefaultAsync<Nullable<DateTime>>(sql, {| target_user_id = targetUserId |})
            return if at.HasValue then Some at.Value else None
        }

    /// Stamps `targetUserId` as roasted at `roastedAt` — called only after a successful
    /// `/roast` reply actually went out (never on a "no data"/cooldown/failed attempt).
    /// `roastedAt` is caller-supplied (the app's `TimeProvider`, matching `LastRoastedAt`'s
    /// caller-side cooldown comparison) rather than SQL `NOW()` — the rest of the codebase
    /// (e.g. `BotService.logRow`'s `sent_at`) never mixes the app's `TimeProvider` with the
    /// database's own wall clock, and TEST_MODE's `FakeTimeProvider` would otherwise never
    /// agree with a DB-side `NOW()`.
    member _.RecordRoast(targetUserId: int64, roastedAt: DateTime) : Task<unit> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
INSERT INTO roast_cooldown (target_user_id, last_roasted_at) VALUES (@target_user_id, @roasted_at)
ON CONFLICT (target_user_id) DO UPDATE SET last_roasted_at = EXCLUDED.last_roasted_at;
"""
            let! _ = conn.ExecuteAsync(sql, {| target_user_id = targetUserId; roasted_at = roastedAt |})
            return ()
        }

    /// Most recent `/song` timestamp for `userId`, or `None` if they've never generated one
    /// — `SONG_COOLDOWN_SECONDS`'s cooldown check (per-INVOKER, unlike `/roast`'s
    /// per-TARGET cooldown — `/song` has no target concept, just a pricey/slow call the
    /// same user shouldn't be able to spam).
    member _.LastSongAt(userId: int64) : Task<DateTime option> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql = "SELECT last_song_at FROM song_cooldown WHERE user_id = @user_id;"
            let! at = conn.QuerySingleOrDefaultAsync<Nullable<DateTime>>(sql, {| user_id = userId |})
            return if at.HasValue then Some at.Value else None
        }

    /// Stamps `userId` as having generated a song at `songAt` — called only after a
    /// successful `/song` reply actually went out (never on a cooldown/empty-prompt/
    /// too-long/failed attempt), same convention as `RecordRoast`. `songAt` is caller-
    /// supplied (the app's `TimeProvider`) for the same `TEST_MODE`/`FakeTimeProvider`
    /// reasoning `RecordRoast`'s doc comment explains.
    member _.RecordSong(userId: int64, songAt: DateTime) : Task<unit> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
INSERT INTO song_cooldown (user_id, last_song_at) VALUES (@user_id, @song_at)
ON CONFLICT (user_id) DO UPDATE SET last_song_at = EXCLUDED.last_song_at;
"""
            let! _ = conn.ExecuteAsync(sql, {| user_id = userId; song_at = songAt |})
            return ()
        }

    /// Inserts one `karma` row (`/awards`, one per awarded title). `userId` is `None` when
    /// the LLM's "user" field couldn't be resolved to a known `message_log.username` — the
    /// row is still kept (with the raw `username` text) so the announcement itself is never
    /// lost, just not queryable by `/karma <user>` for that award.
    member _.InsertKarmaAward(userId: int64 option, username: string, title: string, evidence: string) : Task<unit> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
INSERT INTO karma (user_id, username, title, evidence) VALUES (@user_id, @username, @title, @evidence);
"""
            let! _ =
                conn.ExecuteAsync(
                    sql,
                    {| user_id = toNullable userId
                       username = username
                       title = title
                       evidence = evidence |})
            return ()
        }

    /// Total `karma` rows for `userId` — `/karma`'s headline count.
    member _.KarmaCount(userId: int64) : Task<int64> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql = "SELECT COUNT(*) FROM karma WHERE user_id = @user_id;"
            return! conn.QuerySingleAsync<int64>(sql, {| user_id = userId |})
        }

    /// `userId`'s newest `limit` karma titles, newest first — `/karma`'s "last 3" listing.
    member _.KarmaNewestTitles(userId: int64, limit: int) : Task<string[]> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT title FROM karma WHERE user_id = @user_id ORDER BY awarded_at DESC LIMIT @limit;
"""
            let! rows = conn.QueryAsync<string>(sql, {| user_id = userId; limit = limit |})
            return rows |> Seq.toArray
        }

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

    /// Count of `llm_usage` rows for `userId` whose `kind` is one of `kinds`, since `since`
    /// (inclusive) — S10 PR1's NL_TOOLS_RATE_LIMIT_PER_HOUR check (ToolExecutorService),
    /// mirroring UsageTotals' query shape but scoped to one user and a kind allowlist
    /// instead of a global time window.
    member _.ToolCallCountSince(userId: int64, kinds: string list, since: DateTime) : Task<int64> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT COUNT(*) FROM llm_usage
WHERE user_id = @user_id AND kind = ANY(@kinds) AND called_at >= @since;
"""
            return! conn.QuerySingleAsync<int64>(sql, {| user_id = userId; kinds = kinds |> List.toArray; since = since |})
        }

    // ── Proactive behavior (Slice 8: morning digest, interjections, meme reactions) ────
    //
    // The morning digest reuses HumanMessagesSince (above, /awards'/quote's query) for
    // both its "active enough" threshold and its transcript — no new table. Interjections
    // read straight off message_log too (below); meme reactions need no DB support at all
    // (S6's SetMessageReaction path, no new state).

    /// Non-bot message count + distinct author count in `chatId` since `since` — Slice 8's
    /// willingness-gated-interjection burst check (`BURST_MSGS`/`BURST_SPEAKERS`).
    member _.BurstStats(chatId: int64, since: DateTime) : Task<BurstStatsRow> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT COUNT(*) AS message_count, COUNT(DISTINCT user_id) AS distinct_users
FROM message_log
WHERE chat_id = @chat_id AND is_bot = FALSE AND sent_at >= @since;
"""
            return! conn.QuerySingleAsync<BurstStatsRow>(sql, {| chat_id = chatId; since = since |})
        }

    /// True when `chatId` has at least one bot (`is_bot = TRUE`) `message_log` row strictly
    /// after `since` — Slice 8's interjection cooldown check. A fresh interjection is logged
    /// the same as any other bot reply, so it naturally self-cools the chat for the next one.
    /// Deliberately `sent_at > @since`, not `>=`: `since` is `now - INTERJECT_COOLDOWN_MINUTES`,
    /// and by causality no message's `sent_at` can be later than the `now` used to compute it,
    /// so with `INTERJECT_COOLDOWN_MINUTES=0` (`since = now`) a `>=` comparison degenerates to
    /// "was any bot message EVER logged in this chat, at exactly `now` or earlier" — always
    /// true once the bot has spoken once — instead of "no cooldown at all" as `0` should mean.
    /// This only bites under TEST_MODE's frozen `FakeTimeProvider` (`Program.fs`), where every
    /// `sent_at` stamped during a whole real-test run shares the identical instant (see
    /// `BotService.logRow`/`DigestService.logRow`'s `time.GetUtcNow()`) — real production
    /// traffic uses a live, monotonically-advancing clock, so `sent_at` is always strictly
    /// less than a `since` computed afterward and `>` vs `>=` never mattered there.
    member _.HasBotMessageSince(chatId: int64, since: DateTime) : Task<bool> =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql = "SELECT EXISTS(SELECT 1 FROM message_log WHERE chat_id = @chat_id AND is_bot = TRUE AND sent_at > @since);"
            return! conn.QuerySingleAsync<bool>(sql, {| chat_id = chatId; since = since |})
        }

    // ── /sql (Slice 9 stretch: admin-gated natural-language SQL analytics) ─────────────

    /// Executes an already-`SqlGuard.validate`d, already-`LIMIT`-wrapped SELECT/WITH
    /// statement over a FRESH connection with `SET default_transaction_read_only = on`
    /// (belt-and-braces alongside SqlGuard's text-level check — even a query that somehow
    /// slipped past the guard could only ever SELECT in this session) and a 5-second
    /// command timeout. Returns column names + rows-of-strings (every value rendered via
    /// `.ToString()`, `NULL` for a null cell — `/sql`'s reply is a plain rendered table,
    /// not a typed result set) on success, or the exception message on failure (a bad
    /// query, e.g. an unknown column, still reaches here — the caller renders it as a
    /// short RU error alongside the offending SQL).
    member _.ExecuteReadOnlySelect(sql: string) : Task<Result<string list * string[] list, string>> =
        task {
            try
                use conn = new NpgsqlConnection(connString)
                do! conn.OpenAsync()
                use roCmd = conn.CreateCommand()
                roCmd.CommandText <- "SET default_transaction_read_only = on;"
                let! _ = roCmd.ExecuteNonQueryAsync()

                use cmd = conn.CreateCommand()
                cmd.CommandText <- sql
                cmd.CommandTimeout <- 5
                use! reader = cmd.ExecuteReaderAsync()
                let columns = [ for i in 0 .. reader.FieldCount - 1 -> reader.GetName(i) ]
                let rows = ResizeArray<string[]>()
                let mutable go = true
                while go do
                    let! has = reader.ReadAsync()
                    if not has then
                        go <- false
                    else
                        rows.Add(
                            [| for i in 0 .. reader.FieldCount - 1 ->
                                   if reader.IsDBNull(i) then "NULL" else string (reader.GetValue(i)) |])
                return Ok(columns, rows |> List.ofSeq)
            with ex ->
                return Error ex.Message
        }

    interface IUsageRecorder with
        member this.Record(kind, model, inputTokens, outputTokens, costUsd, ctx: UsageContext) =
            this.RecordLlmUsage(kind, model, inputTokens, outputTokens, costUsd, ctx.ChatId, ctx.UserId)
