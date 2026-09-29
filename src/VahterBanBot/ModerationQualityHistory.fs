namespace VahterBanBot

open System
open System.Data
open System.Threading
open BotInfra
open Dapper
open Npgsql
open VahterBanBot.Types
open VahterBanBot.Utils

type ModerationQualityHistory(connectionString: string, clock: TimeProvider) =
    let store = EventStore(connectionString, "event", eventJsonOpts, "event_snapshot")
    let setting (conn: NpgsqlConnection) name fallback = task {
        let! value = conn.QuerySingleOrDefaultAsync<string>("SELECT value FROM bot_setting WHERE key = @name", {| name = name |})
        return match Int32.TryParse value with true, n when n > 0 -> n | _ -> fallback
    }

    let rebuild (conn: NpgsqlConnection) (day: DateTime) (ct: CancellationToken) = task {
        use! tx = conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct)
        let! candidates = conn.QueryAsync<string>(CommandDefinition("""
            SELECT DISTINCT stream_id FROM event
            WHERE event_type = 'MlScoredMessage' AND created_at >= @day AND created_at < @finish
            """, {| day = day; finish = day.AddDays 1.0 |}, tx, cancellationToken = ct))
        let counts = Collections.Generic.Dictionary<string * string * string option * string, int64>()
        let increment cohort system model code =
            let key = cohort, system, model, code
            let found, count = counts.TryGetValue key
            counts[key] <- (if found then count else 0L) + 1L
        for batch in candidates |> Seq.chunkBySize 250 do
            ct.ThrowIfCancellationRequested()
            let suffixes = batch |> Array.map (fun s -> s.Substring("detection:".Length))
            let ids = [ for s in suffixes do for prefix in ["message:"; "moderation:"; "detection:"] do yield prefix + s ]
            let! rows = store.ReadRawEventsForStreams(conn, tx, ids)
            let users =
                [ for s in suffixes do
                    for r in rows["message:" + s] do
                        match store.Deserialize<MessageEvent> r with
                        | MessageReceived e -> yield $"user:{e.userId}"
                        | _ -> () ] |> List.distinct
            let! bans = conn.QueryAsync<RawEvent>(CommandDefinition("""
                SELECT id, stream_id, stream_version, event_type, data::text, created_at FROM event
                WHERE stream_id = ANY(@users) AND event_type IN ('UserBanned', 'UserUnbanned')
                """, {| users = List.toArray users |}, tx, cancellationToken = ct))
            let unbans = bans |> Seq.groupBy (fun r -> r.stream_id) |> Seq.collect (fun (_, rs) -> ModerationQuality.unbans store (Seq.toList rs)) |> Seq.toList
            for s in suffixes do
                let parts = s.Split ':'
                let corrections = unbans |> List.filter (fun c -> c.ChatId = Int64.Parse parts[0] && c.MessageId = Int64.Parse parts[1])
                match ModerationQuality.evaluate store rows["message:" + s] rows["moderation:" + s] rows["detection:" + s] corrections with
                | Some evaluation when evaluation.EvaluatedAt.Date = day ->
                    for c in evaluation.Contributions do increment c.Cohort c.System c.LlmModel c.Outcome.Code
                | _ -> ()
        for system in ["pipeline"; "ml"] do
            let key = "all_scored", system, None, "tp"
            if not (counts.ContainsKey key) then counts[key] <- 0L
        let! _ = conn.ExecuteAsync(CommandDefinition("DELETE FROM moderation_quality_daily WHERE day = @day", {| day = day |}, tx, cancellationToken = ct))
        for ((cohort, system, model), values) in counts |> Seq.groupBy (fun kv -> let c, s, m, _ = kv.Key in c, s, m) do
            let value code = values |> Seq.sumBy (fun kv -> let _, _, _, c = kv.Key in if c = code then kv.Value else 0L)
            let! _ = conn.ExecuteAsync(CommandDefinition("""
                INSERT INTO moderation_quality_daily (day, cohort, system, llm_model, tp, tn, fp, fn, abstained, unresolved, excluded)
                VALUES (@day, @cohort, @system, @model, @tp, @tn, @fp, @fn, @abstained, @unresolved, @excluded)
                """, {| day = day; cohort = cohort; system = system; model = Option.toObj model;
                     tp = value "tp"; tn = value "tn"; fp = value "fp"; fn = value "fn";
                     abstained = value "abstained"; unresolved = value "unresolved"; excluded = value "excluded" |}, tx, cancellationToken = ct))
            ()
        do! tx.CommitAsync ct
    }

    member _.Run(day: DateTime option, ct: CancellationToken) = task {
        let connection = NpgsqlConnectionStringBuilder(connectionString)
        connection.Pooling <- false
        use conn = new NpgsqlConnection(connection.ConnectionString)
        do! conn.OpenAsync ct
        let! settling = setting conn "QUALITY_SETTLING_DAYS" 14
        let cutoff = clock.GetUtcNow().UtcDateTime.Date.AddDays(-float settling)
        if day |> Option.exists (fun d -> d.Kind <> DateTimeKind.Utc || d <> d.Date || d >= cutoff) then
            invalidArg (nameof day) "Day must be a mature UTC date"
        let! acquired = conn.QuerySingleAsync<bool>("SELECT pg_try_advisory_lock(734829105)")
        if not acquired then invalidOp "A quality history job is already running"
        let! limit = setting conn "QUALITY_BACKFILL_DAYS" 7
        let! days = task {
            match day with
            | Some d -> return [d]
            | None ->
                let! result = conn.QueryAsync<DateTime>(CommandDefinition("""
                    SELECT d AT TIME ZONE 'UTC' FROM generate_series(
                        (SELECT min(created_at) AT TIME ZONE 'UTC' FROM event WHERE event_type = 'MlScoredMessage')::date::timestamp,
                        (@cutoff AT TIME ZONE 'UTC') - interval '1 day', interval '1 day') d
                    WHERE NOT EXISTS (SELECT 1 FROM moderation_quality_daily q WHERE q.day = d::date AND q.cohort = 'all_scored' AND q.system = 'pipeline')
                    ORDER BY d LIMIT @limit
                    """, {| cutoff = cutoff; limit = min limit 31 |}, cancellationToken = ct))
                return Seq.toList result
        }
        for d in days do do! rebuild conn d ct
        return days.Length
    }
