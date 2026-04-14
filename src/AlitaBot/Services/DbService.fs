namespace AlitaBot.Services

open System
open System.Collections.Generic
open System.Threading.Tasks
open Dapper
open Npgsql
open AlitaBot

type DbService(dataSource: NpgsqlDataSource, timeProvider: TimeProvider, connString: string) =

    let utcNow () = timeProvider.GetUtcNow().UtcDateTime

    // ── Message log ──────────────────────────────────────────────────────────

    member _.LogMessage(userId: int64, chatId: int64, message: string) : Task = task {
        use! conn = dataSource.OpenConnectionAsync()
        //language=postgresql
        let sql =
            """
INSERT INTO message_log (user_id, chat_id, message, sent_at)
VALUES (@userId, @chatId, @message, @sentAt);
            """
        let! _ = conn.ExecuteAsync(sql, {| userId = userId; chatId = chatId; message = message; sentAt = utcNow() |})
        return ()
    }

    member _.GetRecentMessages(chatId: int64, limit: int) : Task<MessageLogRow array> = task {
        use! conn = dataSource.OpenConnectionAsync()
        //language=postgresql
        let sql =
            """
SELECT id, user_id, chat_id, message, sent_at
FROM message_log
WHERE chat_id = @chatId
ORDER BY sent_at DESC
LIMIT @limit;
            """
        let! rows = conn.QueryAsync<MessageLogRow>(sql, {| chatId = chatId; limit = limit |})
        return rows |> Seq.toArray |> Array.rev   // chronological order
    }

    member _.GetUserMessagesLastDay(userId: int64) : Task<string array> = task {
        use! conn = dataSource.OpenConnectionAsync()
        //language=postgresql
        let sql =
            """
SELECT message
FROM message_log
WHERE user_id = @userId
  AND sent_at >= NOW() - INTERVAL '24 hours'
ORDER BY sent_at;
            """
        let! rows = conn.QueryAsync<string>(sql, {| userId = userId |})
        return rows |> Seq.toArray
    }

    member _.GetActiveUsersLastDay() : Task<int64 array> = task {
        use! conn = dataSource.OpenConnectionAsync()
        //language=postgresql
        let sql =
            """
SELECT DISTINCT user_id
FROM message_log
WHERE sent_at >= NOW() - INTERVAL '24 hours';
            """
        let! rows = conn.QueryAsync<int64>(sql)
        return rows |> Seq.toArray
    }

    member _.PurgeOldMessages(retentionDays: int) : Task = task {
        use! conn = dataSource.OpenConnectionAsync()
        //language=postgresql
        let sql =
            """
DELETE FROM message_log
WHERE sent_at < NOW() - (@retentionDays || ' days')::INTERVAL;
            """
        let! _ = conn.ExecuteAsync(sql, {| retentionDays = retentionDays |})
        return ()
    }

    // ── Person dossier ───────────────────────────────────────────────────────

    member _.UpsertPerson(userId: int64, username: string, displayName: string) : Task = task {
        use! conn = dataSource.OpenConnectionAsync()
        //language=postgresql
        let sql =
            """
INSERT INTO person_dossier (user_id, username, display_name, updated_at)
VALUES (@userId, @username, @displayName, @now)
ON CONFLICT (user_id) DO UPDATE
    SET username     = EXCLUDED.username,
        display_name = EXCLUDED.display_name,
        updated_at   = EXCLUDED.updated_at;
            """
        let! _ = conn.ExecuteAsync(sql, {| userId = userId; username = username; displayName = displayName; now = utcNow() |})
        return ()
    }

    member _.GetDossier(userId: int64) : Task<PersonDossier option> = task {
        use! conn = dataSource.OpenConnectionAsync()
        //language=postgresql
        let sql =
            """
SELECT user_id, username, display_name, summary, updated_at
FROM person_dossier
WHERE user_id = @userId;
            """
        let! row = conn.QuerySingleOrDefaultAsync<PersonDossier>(sql, {| userId = userId |})
        return if box row = null then None else Some row
    }

    member _.UpdateDossierSummary(userId: int64, summary: string) : Task = task {
        use! conn = dataSource.OpenConnectionAsync()
        //language=postgresql
        let sql =
            """
UPDATE person_dossier
SET summary    = @summary,
    updated_at = @now
WHERE user_id = @userId;
            """
        let! _ = conn.ExecuteAsync(sql, {| userId = userId; summary = summary; now = utcNow() |})
        return ()
    }

    // ── Interaction memory ───────────────────────────────────────────────────

    member _.InsertMemory(userId: int64, content: string, embedding: float32[]) : Task = task {
        use! conn = dataSource.OpenConnectionAsync()
        //language=postgresql
        let sql =
            """
INSERT INTO interaction_memory (user_id, content, embedding, created_at)
VALUES (@userId, @content, @embedding::vector, @now);
            """
        let! _ = conn.ExecuteAsync(sql, {| userId = userId; content = content; embedding = Pgvector.Vector(embedding); now = utcNow() |})
        return ()
    }

    /// Returns the top-K most semantically similar memories for a user.
    member _.RecallMemories(userId: int64, queryEmbedding: float32[], topK: int) : Task<string array> = task {
        use! conn = dataSource.OpenConnectionAsync()
        //language=postgresql
        let sql =
            """
SELECT content
FROM interaction_memory
WHERE user_id = @userId
  AND embedding IS NOT NULL
ORDER BY embedding <=> @embedding::vector
LIMIT @topK;
            """
        let! rows = conn.QueryAsync<string>(sql, {| userId = userId; embedding = Pgvector.Vector(queryEmbedding); topK = topK |})
        return rows |> Seq.toArray
    }

    // ── News summaries ───────────────────────────────────────────────────────

    member _.InsertNewsSummary(sourceUrl: string, summary: string) : Task = task {
        use! conn = dataSource.OpenConnectionAsync()
        //language=postgresql
        let sql =
            """
INSERT INTO news_summary (source_url, summary, fetched_at)
VALUES (@sourceUrl, @summary, @now);
            """
        let! _ = conn.ExecuteAsync(sql, {| sourceUrl = sourceUrl; summary = summary; now = utcNow() |})
        return ()
    }

    member _.GetOldestUnpostedNews() : Task<NewsItem option> = task {
        use! conn = dataSource.OpenConnectionAsync()
        //language=postgresql
        let sql =
            """
SELECT id, source_url, summary, fetched_at, posted
FROM news_summary
WHERE posted = false
ORDER BY fetched_at
LIMIT 1;
            """
        let! row = conn.QuerySingleOrDefaultAsync<NewsItem>(sql)
        return if box row = null then None else Some row
    }

    member _.MarkNewsPosted(id: int64) : Task = task {
        use! conn = dataSource.OpenConnectionAsync()
        //language=postgresql
        let sql = "UPDATE news_summary SET posted = true WHERE id = @id;"
        let! _ = conn.ExecuteAsync(sql, {| id = id |})
        return ()
    }

    // ── Scheduled job helpers (delegates to BotInfra) ──────────────────────

    member _.TryAcquireJob(jobName: string, scheduledTime: TimeSpan, podId: string) : Task<bool> =
        BotInfra.ScheduledJobs.tryAcquire connString timeProvider jobName scheduledTime podId

    /// Overload for interval-based jobs: acquires if last_completed_at < now - interval.
    member _.TryAcquireIntervalJob(jobName: string, interval: TimeSpan, podId: string) : Task<bool> = task {
        use conn = new NpgsqlConnection(connString)
        let now = utcNow()
        //language=postgresql
        let sql =
            """
UPDATE scheduled_job
SET locked_until = @now + INTERVAL '1 hour',
    locked_by    = @podId
WHERE job_name = @jobName
  AND (locked_until IS NULL OR locked_until < @now)
  AND (last_completed_at IS NULL OR last_completed_at < @now - @interval)
RETURNING job_name;
            """
        let! result = conn.QueryAsync<string>(sql, {| jobName = jobName; podId = podId; now = now; interval = interval |})
        return Seq.length result > 0
    }

    member _.CompleteJob(jobName: string) : Task =
        BotInfra.ScheduledJobs.complete connString timeProvider jobName
