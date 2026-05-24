namespace CouponHubBot.Services

open System
open System.Data
open System.Runtime.ExceptionServices
open System.Threading.Tasks
open Dapper
open Npgsql
open CouponHubBot
open CouponHubBot.Utils
open BotInfra

type TakeCouponResult =
    | Taken of Coupon
    | NotFoundOrNotAvailable
    | LimitReached

[<RequireQualifiedAccess>]
type AddCouponResult =
    | Added of Coupon
    | Expired
    | DuplicatePhoto of existingCouponId: int
    | DuplicateBarcode of existingCouponId: int

[<RequireQualifiedAccess>]
type VoidCouponResult =
    | Voided of coupon: Coupon * takenByUserId: int64 option
    | NotFoundOrNotAllowed

[<CLIMutable>]
type PendingAddFlow =
    { user_id: int64
      stage: string
      photo_file_id: string | null
      value: Nullable<decimal>
      min_check: Nullable<decimal>
      expires_at: Nullable<DateOnly>
      barcode_text: string | null
      valid_from: Nullable<DateOnly>
      updated_at: DateTime }

[<CLIMutable>]
type PendingAddBatch =
    { id: int64
      user_id: int64
      media_group_id: string
      bulk_chat_id: int64
      bulk_message_id: Nullable<int>
      status: string
      created_at: DateTime
      updated_at: DateTime }

[<CLIMutable>]
type PendingAddBatchItem =
    { id: int64
      batch_id: int64
      seq: int
      photo_file_id: string
      photo_message_id: int
      status: string
      value: Nullable<decimal>
      min_check: Nullable<decimal>
      expires_at: Nullable<DateOnly>
      barcode_text: string | null
      valid_from: Nullable<DateOnly>
      failure_note: string | null
      created_at: DateTime }

[<CLIMutable>]
type UserEventCount =
    { user_id: int64
      username: string | null
      first_name: string | null
      count: int64 }

[<CLIMutable>]
type OverdueTakenUser =
    { user_id: int64
      overdue_count: int }

[<CLIMutable>]
type EventTypeCountRow =
    { event_type: string
      count: int64 }

[<CLIMutable>]
type CouponOutcomes =
    { used_count: int64
      expired_count: int64
      active_count: int64
      voided_count: int64
      total_count: int64 }

[<CLIMutable>]
type ChatMessageRow =
    { user_id: int64
      message_id: int
      text: string | null
      has_photo: bool
      has_document: bool
      reply_to_message_id: Nullable<int>
      created_at: DateTime }

type DbService(connString: string, timeProvider: TimeProvider, maxTakenCoupons: int) =
    let utcNow () = timeProvider.GetUtcNow().UtcDateTime
    let todayUtc () = DateOnly.FromDateTime(utcNow ())

    let openConn() = task {
        let conn = new NpgsqlConnection(connString)
        do! conn.OpenAsync()
        return conn
    }

    let insertEvent (conn: NpgsqlConnection) (tx: IDbTransaction) (couponId: int) (userId: int64) (eventType: string) =
        //language=postgresql
        let sql =
            """
INSERT INTO coupon_event (coupon_id, user_id, event_type)
VALUES (@coupon_id, @user_id, @event_type);
"""
        conn.ExecuteAsync(sql, {| coupon_id = couponId; user_id = userId; event_type = eventType |}, tx) |> taskIgnore

    member _.UpsertUser(user: DbUser) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
INSERT INTO "user" (id, username, first_name, last_name, created_at, updated_at)
VALUES (@id, @username, @first_name, @last_name, @created_at, @updated_at)
ON CONFLICT (id) DO UPDATE
SET username   = EXCLUDED.username,
    first_name = EXCLUDED.first_name,
    last_name  = EXCLUDED.last_name,
    updated_at = GREATEST(EXCLUDED.updated_at, "user".updated_at)
RETURNING *;
"""
            let! inserted = conn.QuerySingleAsync<DbUser>(sql, user)
            return inserted
        }

    member _.TryAddCoupon(ownerId, photoFileId, value, minCheck: decimal, expiresAt: DateOnly, barcodeText: string | null, ?validFrom: DateOnly) =
        task {
            let todayUtc = todayUtc ()
            if expiresAt < todayUtc then
                return AddCouponResult.Expired
            else
                use! conn = openConn()
                use tx = conn.BeginTransaction(IsolationLevel.ReadCommitted)

                // Check duplicate by photo_file_id (always).
                //language=postgresql
                let dupPhotoSql =
                    """
SELECT id
FROM coupon
WHERE photo_file_id = @photo_file_id
LIMIT 1;
"""

                let! dupPhoto =
                    conn.QuerySingleOrDefaultAsync<int>(
                        dupPhotoSql,
                        {| photo_file_id = photoFileId |},
                        tx
                    )

                if dupPhoto <> 0 then
                    do! tx.RollbackAsync()
                    return AddCouponResult.DuplicatePhoto dupPhoto
                else
                    // Check duplicate by barcode only when barcode is known.
                    let hasBarcode = not (String.IsNullOrWhiteSpace barcodeText)
                    
                    // Query duplicate barcode id only when barcode is known; otherwise treat as no-dup (0).
                    //language=postgresql
                    let dupBarcodeSql =
                        """
SELECT id
FROM coupon
WHERE barcode_text = @barcode_text
  AND expires_at >= @today
LIMIT 1;
"""

                    let! dupBarcode =
                        if hasBarcode then
                            conn.QuerySingleOrDefaultAsync<int>(
                                dupBarcodeSql,
                                {| barcode_text = barcodeText
                                   today = todayUtc |},
                                tx
                            )
                        else
                            Task.FromResult 0

                    if dupBarcode <> 0 then
                        do! tx.RollbackAsync()
                        return AddCouponResult.DuplicateBarcode dupBarcode
                    else

                        // Insert coupon.
                        //language=postgresql
                        let insertSql =
                            """
INSERT INTO coupon (owner_id, photo_file_id, value, min_check, expires_at, barcode_text, valid_from, status)
VALUES (@owner_id, @photo_file_id, @value, @min_check, @expires_at, @barcode_text, @valid_from, 'available')
RETURNING *;
"""
                        let validFromValue =
                            match validFrom with
                            | Some vf -> Nullable(vf)
                            | None -> Nullable()

                        try
                            let! coupon =
                                conn.QuerySingleAsync<Coupon>(
                                    insertSql,
                                    {| owner_id = ownerId
                                       photo_file_id = photoFileId
                                       value = value
                                       min_check = minCheck
                                       expires_at = expiresAt
                                       barcode_text = barcodeText
                                       valid_from = validFromValue |},
                                    tx
                                )
                            do! insertEvent conn tx coupon.id ownerId "added"
                            do! tx.CommitAsync()
                            return AddCouponResult.Added coupon
                        with
                        | :? PostgresException as pgEx
                            when pgEx.SqlState = "23505"
                                 && pgEx.ConstraintName = "coupon_barcode_active_uniq" ->
                            do! tx.RollbackAsync()
                            // Race condition: another transaction inserted the same barcode concurrently.
                            // Look up the winning coupon by the exact constraint key to return its ID.
                            //language=postgresql
                            let dupBarcodeByKeySql =
                                """
SELECT id
FROM coupon
WHERE barcode_text = @barcode_text
  AND expires_at = @expires_at
  AND status IN ('available', 'taken')
ORDER BY id
LIMIT 1;
"""
                            let! existingId =
                                conn.QuerySingleOrDefaultAsync<int>(
                                    dupBarcodeByKeySql,
                                    {| barcode_text = barcodeText
                                       expires_at = expiresAt |}
                                )
                            if existingId = 0 then
                                // The winning row was not found — the concurrent transaction may have
                                // rolled back by the time we looked. Re-raise preserving the stack trace.
                                ExceptionDispatchInfo.Throw pgEx
                                return Unchecked.defaultof<AddCouponResult>
                            else
                                return AddCouponResult.DuplicateBarcode existingId
                        | :? PostgresException as pgEx
                            when pgEx.SqlState = "23505"
                                 && pgEx.ConstraintName = "coupon_photo_file_id_uniq" ->
                            do! tx.RollbackAsync()
                            // Race condition: another transaction inserted the same photo concurrently.
                            // Look up the winning coupon to return its ID.
                            let! existingId =
                                conn.QuerySingleOrDefaultAsync<int>(
                                    dupPhotoSql,
                                    {| photo_file_id = photoFileId |}
                                )
                            if existingId = 0 then
                                // The winning row was not found — the concurrent transaction may have
                                // rolled back by the time we looked. Re-raise preserving the stack trace.
                                ExceptionDispatchInfo.Throw pgEx
                                return Unchecked.defaultof<AddCouponResult>
                            else
                                return AddCouponResult.DuplicatePhoto existingId
        }

    member _.GetAvailableCoupons() =
        task {
            use! conn = openConn()
            let today = todayUtc ()
            //language=postgresql
            let sql =
                """
SELECT *
FROM coupon
WHERE status = 'available'
  AND expires_at >= @today
  AND (valid_from IS NULL OR valid_from <= @today)
ORDER BY expires_at, id;
"""
            let! coupons = conn.QueryAsync<Coupon>(sql, {| today = today |})
            return coupons |> Seq.toArray
        }

    member _.GetCouponById(couponId) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql = "SELECT * FROM coupon WHERE id = @coupon_id"
            let! coupons = conn.QueryAsync<Coupon>(sql, {| coupon_id = couponId |})
            return coupons |> Seq.tryHead
        }
        
    member _.GetUserById(userId) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql = """SELECT * FROM "user" WHERE id = @user_id"""
            let! users = conn.QueryAsync<DbUser>(sql, {| user_id = userId |})
            return users |> Seq.tryHead
        }

    member _.GetCouponsByOwner(ownerId) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT *
FROM coupon
WHERE owner_id = @owner_id
ORDER BY created_at DESC, id DESC;
"""
            let! coupons = conn.QueryAsync<Coupon>(sql, {| owner_id = ownerId |})
            return coupons |> Seq.toArray
        }

    member _.GetCouponsTakenBy(userId) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT *
FROM coupon
WHERE taken_by = @user_id
  AND status = 'taken'
ORDER BY taken_at DESC NULLS LAST, id DESC;
"""
            let! coupons = conn.QueryAsync<Coupon>(sql, {| user_id = userId |})
            return coupons |> Seq.toArray
        }

    member _.GetUserStats(userId) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT event_type, COUNT(*)::bigint AS count
FROM coupon_event
WHERE user_id = @user_id
GROUP BY event_type;
"""
            let! rows = conn.QueryAsync<EventTypeCountRow>(sql, {| user_id = userId |})

            let counts =
                rows
                |> Seq.fold (fun acc r -> acc |> Map.add r.event_type r.count) Map.empty

            let get (eventType: string) =
                counts
                |> Map.tryFind eventType
                |> Option.defaultValue 0L

            return get "added", get "taken", get "returned", get "used", get "voided"
        }

    member _.GetPersonalCouponOutcomes(userId: int64) =
        task {
            use! conn = openConn()
            let today = todayUtc ()
            //language=postgresql
            let sql =
                """
SELECT
    COUNT(*) FILTER (WHERE status = 'used')::bigint                                          AS used_count,
    COUNT(*) FILTER (WHERE status IN ('available','taken') AND expires_at < @today)::bigint  AS expired_count,
    COUNT(*) FILTER (WHERE status IN ('available','taken') AND expires_at >= @today)::bigint AS active_count,
    COUNT(*) FILTER (WHERE status = 'voided')::bigint                                        AS voided_count,
    COUNT(*)::bigint                                                                         AS total_count
FROM coupon
WHERE owner_id = @user_id;
"""
            return! conn.QuerySingleAsync<CouponOutcomes>(sql, {| user_id = userId; today = today |})
        }

    member _.GetGlobalCouponStats() =
        task {
            use! conn = openConn()
            let today = todayUtc ()
            //language=postgresql
            let sql =
                """
SELECT
    COUNT(*) FILTER (WHERE status = 'used')::bigint                                          AS used_count,
    COUNT(*) FILTER (WHERE status IN ('available','taken') AND expires_at < @today)::bigint  AS expired_count,
    COUNT(*) FILTER (WHERE status IN ('available','taken') AND expires_at >= @today)::bigint AS active_count,
    COUNT(*) FILTER (WHERE status = 'voided')::bigint                                        AS voided_count,
    COUNT(*)::bigint                                                                         AS total_count
FROM coupon;
"""
            return! conn.QuerySingleAsync<CouponOutcomes>(sql, {| today = today |})
        }

    member _.TryTakeCoupon(couponId, takerId) =
        task {
            use! conn = openConn()
            use tx = conn.BeginTransaction(IsolationLevel.ReadCommitted)
            let today = todayUtc ()
            let takenAt = utcNow ()

            // Serialize concurrent take attempts for the same user to avoid race conditions on the coupons limit.
            //language=postgresql
            let lockUserSql =
                """
SELECT id
FROM "user"
WHERE id = @taker_id
FOR UPDATE;
"""
            let! _lockedUser =
                conn.QuerySingleOrDefaultAsync<int64>(
                    lockUserSql,
                    {| taker_id = takerId |},
                    tx
                )

            // Enforce max simultaneously taken coupons per user.
            //language=postgresql
            let countSql =
                """
SELECT COUNT(*)::int
FROM coupon
WHERE taken_by = @taker_id
  AND status = 'taken';
"""
            let! takenCount =
                conn.QuerySingleAsync<int>(
                    countSql,
                    {| taker_id = takerId |},
                    tx
                )

            if takenCount >= maxTakenCoupons then
                do! tx.RollbackAsync()
                return LimitReached
            else
            // Atomic: only one taker wins (status must be available)
            //language=postgresql
            let sql =
                """
UPDATE coupon
SET status = 'taken',
    taken_by = @taker_id,
    taken_at = @taken_at
WHERE id = @coupon_id
  AND status = 'available'
  AND expires_at >= @today
  AND (valid_from IS NULL OR valid_from <= @today)
RETURNING *;
"""
            let! updated =
                conn.QueryAsync<Coupon>(
                    sql,
                    {| coupon_id = couponId
                       taker_id = takerId
                       taken_at = takenAt
                       today = today |},
                    tx
                )

            match updated |> Seq.tryHead with
            | None ->
                do! tx.RollbackAsync()
                return NotFoundOrNotAvailable
            | Some coupon ->
                do! insertEvent conn tx coupon.id takerId "taken"
                do! tx.CommitAsync()
                return Taken coupon
        }

    member _.MarkUsed(couponId, userId) =
        task {
            use! conn = openConn()
            use tx = conn.BeginTransaction(IsolationLevel.ReadCommitted)

            //language=postgresql
            let sql =
                """
UPDATE coupon
SET status = 'used'
WHERE id = @coupon_id
AND status = 'taken'
AND taken_by = @user_id;
"""
            let! rows = conn.ExecuteAsync(sql, {| coupon_id = couponId; user_id = userId |}, tx)
            if rows = 1 then
                do! insertEvent conn tx couponId userId "used"
                do! tx.CommitAsync()
                return true
            else
                do! tx.RollbackAsync()
                return false
        }

    member _.ReturnToAvailable(couponId, userId) =
        task {
            use! conn = openConn()
            use tx = conn.BeginTransaction(IsolationLevel.ReadCommitted)

            //language=postgresql
            let sql =
                """
UPDATE coupon
SET status = 'available',
    taken_by = NULL,
    taken_at = NULL
WHERE id = @coupon_id
  AND status = 'taken'
  AND taken_by = @user_id;
"""
            let! rows = conn.ExecuteAsync(sql, {| coupon_id = couponId; user_id = userId |}, tx)
            if rows = 1 then
                do! insertEvent conn tx couponId userId "returned"
                do! tx.CommitAsync()
                return true
            else
                do! tx.RollbackAsync()
                return false
        }

    member _.GetExpiringTodayAvailable() =
        task {
            use! conn = openConn()
            let today = todayUtc ()
            //language=postgresql
            let sql =
                """
SELECT *
FROM coupon
WHERE status = 'available'
AND expires_at = @today
ORDER BY id;
"""
            let! coupons = conn.QueryAsync<Coupon>(sql, {| today = today |})
            return coupons |> Seq.toArray
        }

    member _.GetUsersWithOverdueTakenCoupons(nowUtc: DateTime, minAge: TimeSpan) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT taken_by AS user_id, COUNT(*)::int AS overdue_count
FROM coupon
WHERE status = 'taken'
  AND taken_by IS NOT NULL
  AND taken_at IS NOT NULL
  AND taken_at <= (@now_utc - (@min_age_seconds * interval '1 second'))
GROUP BY taken_by
ORDER BY taken_by;
"""
            let! rows =
                conn.QueryAsync<OverdueTakenUser>(
                    sql,
                    {| now_utc = nowUtc
                       min_age_seconds = int64 minAge.TotalSeconds |}
                )
            return rows |> Seq.toArray
        }

    member _.GetUserEventCounts(eventType: string, sinceUtc: DateTime, untilUtc: DateTime) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT e.user_id,
       u.username,
       u.first_name,
       COUNT(*)::bigint AS count
FROM coupon_event e
JOIN "user" u ON u.id = e.user_id
WHERE e.event_type = @event_type
  AND e.created_at >= @since_utc
  AND e.created_at < @until_utc
GROUP BY e.user_id, u.username, u.first_name
ORDER BY count DESC, e.user_id;
"""
            let! rows =
                conn.QueryAsync<UserEventCount>(
                    sql,
                    {| event_type = eventType
                       since_utc = sinceUtc
                       until_utc = untilUtc |}
                )
            return rows |> Seq.toArray
        }

    member _.GetUsersWhoUsedButDidNotAddYesterday(nowUtc: DateTime) =
        task {
            use! conn = openConn()
            // Calculate yesterday's date range in UTC
            let today = DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, 0, 0, 0, DateTimeKind.Utc)
            let yesterdayStart = today.AddDays(-1.0)
            let yesterdayEnd = today
            
            //language=postgresql
            let sql =
                """
SELECT DISTINCT u.user_id
FROM (
    SELECT user_id, MAX(created_at) AS last_used_at
    FROM coupon_event
    WHERE event_type = 'used'
      AND created_at >= @yesterday_start
      AND created_at < @yesterday_end
    GROUP BY user_id
) u
WHERE NOT EXISTS (
    SELECT 1
    FROM coupon_event e
    WHERE e.user_id = u.user_id
      AND e.event_type = 'added'
      AND e.created_at > u.last_used_at
)
ORDER BY u.user_id;
"""
            let! userIds =
                conn.QueryAsync<int64>(
                    sql,
                    {| yesterday_start = yesterdayStart
                       yesterday_end = yesterdayEnd |}
                )
            return userIds |> Seq.toArray
        }

    member _.GetPendingAddFlow(userId: int64) =
        task {
            use! conn = openConn()
            let nowUtc = utcNow ()
            // Expire after 1 hour of inactivity.
            //language=postgresql
            let expireSql =
                """
DELETE FROM pending_add
WHERE user_id = @user_id
  AND updated_at < (@now_utc - interval '1 hour');
"""
            let! _ = conn.ExecuteAsync(expireSql, {| user_id = userId; now_utc = nowUtc |})

            //language=postgresql
            let sql =
                """
SELECT *
FROM pending_add
WHERE user_id = @user_id;
"""
            let! row = conn.QuerySingleOrDefaultAsync<PendingAddFlow>(sql, {| user_id = userId |})
            if obj.ReferenceEquals(row, null) then
                return None
            else
                return Some row
        }

    member _.UpsertPendingAddFlow(flow: PendingAddFlow) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
INSERT INTO pending_add (user_id, stage, photo_file_id, value, min_check, expires_at, barcode_text, valid_from, updated_at)
VALUES (@user_id, @stage, @photo_file_id, @value, @min_check, @expires_at, @barcode_text, @valid_from, @updated_at)
ON CONFLICT (user_id) DO UPDATE
SET stage = EXCLUDED.stage,
    photo_file_id = EXCLUDED.photo_file_id,
    value = EXCLUDED.value,
    min_check = EXCLUDED.min_check,
    expires_at = EXCLUDED.expires_at,
    barcode_text = EXCLUDED.barcode_text,
    valid_from = EXCLUDED.valid_from,
    updated_at = EXCLUDED.updated_at;
"""
            let! _ = conn.ExecuteAsync(sql, flow)
            return ()
        }

    member _.ClearPendingAddFlow(userId: int64) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql = "DELETE FROM pending_add WHERE user_id = @user_id;"
            let! _ = conn.ExecuteAsync(sql, {| user_id = userId |})
            return ()
        }

    member _.SetPendingFeedback(userId: int64) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
INSERT INTO pending_feedback (user_id)
VALUES (@user_id)
ON CONFLICT (user_id) DO UPDATE
SET created_at = EXCLUDED.created_at;
"""
            let! _ = conn.ExecuteAsync(sql, {| user_id = userId |})
            return ()
        }

    /// Deletes pending flag and returns true if it existed and was not expired.
    member _.TryConsumePendingFeedback(userId: int64) =
        task {
            use! conn = openConn()
            let nowUtc = utcNow ()
            // Expire after 24 hours of inactivity.
            //language=postgresql
            let expireSql =
                """
DELETE FROM pending_feedback
WHERE user_id = @user_id
  AND created_at < (@now_utc - interval '24 hours');
"""
            let! _ = conn.ExecuteAsync(expireSql, {| user_id = userId; now_utc = nowUtc |})

            //language=postgresql
            let sql =
                """
DELETE FROM pending_feedback
WHERE user_id = @user_id
RETURNING user_id;
"""
            let! deleted = conn.QueryAsync<int64>(sql, {| user_id = userId |})
            return deleted |> Seq.isEmpty |> not
        }

    member _.ClearPendingFeedback(userId: int64) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql = "DELETE FROM pending_feedback WHERE user_id = @user_id;"
            let! _ = conn.ExecuteAsync(sql, {| user_id = userId |})
            return ()
        }

    member _.VoidCoupon(couponId: int, userId: int64, isAdmin: bool) =
        task {
            use! conn = openConn()
            use tx = conn.BeginTransaction(IsolationLevel.ReadCommitted)
            let today = todayUtc ()

            // First, read the coupon to capture taken_by before we clear it.
            //language=postgresql
            let selectSql =
                """
SELECT *
FROM coupon
WHERE id = @coupon_id
  AND status IN ('available', 'taken')
  AND expires_at >= @today
  AND (@is_admin OR owner_id = @user_id)
FOR UPDATE;
"""
            let! selectRows =
                conn.QueryAsync<Coupon>(
                    selectSql,
                    {| coupon_id = couponId
                       today = today
                       user_id = userId
                       is_admin = isAdmin |},
                    tx
                )

            match selectRows |> Seq.tryHead with
            | None ->
                do! tx.RollbackAsync()
                return VoidCouponResult.NotFoundOrNotAllowed
            | Some original ->
                let takenBy =
                    if original.status = "taken" && original.taken_by.HasValue then
                        Some original.taken_by.Value
                    else
                        None

                //language=postgresql
                let updateSql =
                    """
UPDATE coupon
SET status = 'voided',
    taken_by = NULL,
    taken_at = NULL
WHERE id = @coupon_id;
"""
                let! _ = conn.ExecuteAsync(updateSql, {| coupon_id = couponId |}, tx)

                do! insertEvent conn tx couponId original.owner_id "voided"
                do! tx.CommitAsync()
                return VoidCouponResult.Voided ({ original with status = "voided"; taken_by = Nullable(); taken_at = Nullable() }, takenBy)
        }

    member _.GetVoidableCouponsByOwner(ownerId: int64) =
        task {
            use! conn = openConn()
            let today = todayUtc ()
            //language=postgresql
            let sql =
                """
SELECT *
FROM coupon
WHERE owner_id = @owner_id
  AND status IN ('available', 'taken')
  AND expires_at >= @today
ORDER BY expires_at, id;
"""
            let! coupons = conn.QueryAsync<Coupon>(sql, {| owner_id = ownerId; today = today |})
            return coupons |> Seq.toArray
        }

    member _.GetCouponEventHistory(couponId: int) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql = """
SELECT TO_CHAR(ce.created_at, 'YYYY-MM-DD HH24:MI:SS') AS date,
       COALESCE(u.username, COALESCE(u.first_name, '') || COALESCE(u.last_name, '')) AS "user",
       ce.event_type
FROM coupon_event ce
         JOIN public."user" u ON u.id = ce.user_id
WHERE ce.coupon_id = @couponId
ORDER BY ce.created_at;
"""
            let! rows = conn.QueryAsync<CouponEventHistoryRow>(sql, {| couponId = couponId |})
            return rows |> Seq.toArray
        }

    // ── Chat message monitoring ──────────────────────────────────────

    member _.SaveChatMessage(chatId: int64, messageId: int, userId: int64, text: string | null, hasPhoto: bool, hasDocument: bool, replyToMessageId: Nullable<int>) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
INSERT INTO chat_message (chat_id, message_id, user_id, text, has_photo, has_document, reply_to_message_id)
VALUES (@chat_id, @message_id, @user_id, @text, @has_photo, @has_document, @reply_to_message_id)
ON CONFLICT (chat_id, message_id) DO NOTHING;
"""
            let! _ = conn.ExecuteAsync(sql,
                {| chat_id = chatId
                   message_id = messageId
                   user_id = userId
                   text = text
                   has_photo = hasPhoto
                   has_document = hasDocument
                   reply_to_message_id = replyToMessageId |})
            return ()
        }

    member _.DeleteOldChatMessages(olderThan: DateTime) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql = "DELETE FROM chat_message WHERE created_at < @older_than;"
            let! deleted = conn.ExecuteAsync(sql, {| older_than = olderThan |})
            return deleted
        }

    member _.GetRecentChatMessages(chatId: int64, since: DateTime) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT cm.user_id, cm.message_id, cm.text, cm.has_photo, cm.has_document,
       cm.reply_to_message_id, cm.created_at
FROM chat_message cm
WHERE cm.chat_id = @chat_id
  AND cm.created_at >= @since
ORDER BY cm.created_at;
"""
            let! rows = conn.QueryAsync<ChatMessageRow>(sql, {| chat_id = chatId; since = since |})
            return rows |> Seq.toArray
        }

    // ── User feedback ────────────────────────────────────────────────

    member _.SaveUserFeedback(userId: int64, feedbackText: string | null, hasMedia: bool, telegramMessageId: int) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
INSERT INTO user_feedback (user_id, feedback_text, has_media, telegram_message_id)
VALUES (@user_id, @feedback_text, @has_media, @telegram_message_id)
RETURNING id;
"""
            let! id = conn.QuerySingleAsync<int64>(sql,
                {| user_id = userId
                   feedback_text = feedbackText
                   has_media = hasMedia
                   telegram_message_id = telegramMessageId |})
            return id
        }

    member _.UpdateFeedbackGitHubIssue(feedbackId: int64, issueNumber: int) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql = "UPDATE user_feedback SET github_issue_number = @issue_number WHERE id = @id;"
            let! _ = conn.ExecuteAsync(sql, {| id = feedbackId; issue_number = issueNumber |})
            return ()
        }

    // ── Album upload batches ─────────────────────────────────────────────

    /// Atomic per-user "create-or-find batch + abandon any other active batches"
    /// sequence. Both DB writes happen in one transaction guarded by a per-user
    /// advisory lock so concurrent webhooks from the same user can't both create
    /// a batch and then each abandon the other (the bug we hit when two
    /// truly-simultaneous webhooks landed within the same millisecond).
    ///
    /// Returns:
    ///   batchId   — the batch row for (user, media_group_id) — newly inserted or
    ///               reused.
    ///   isNew     — true if we just inserted; false if an active batch with the
    ///               same media_group_id already existed (e.g. subsequent photos
    ///               of the same album).
    ///   abandoned — the rows DELETEd for this user's OTHER active media_group_ids
    ///               (always empty when isNew = false). The caller iterates this
    ///               to edit each old batch's bulk-confirm message.
    ///
    /// Also reaps stale 'open' batches (>1h old) opportunistically.
    member _.CreateBatchAtomically(userId: int64, mediaGroupId: string, chatId: int64) =
        task {
            use! conn = openConn()
            use tx = conn.BeginTransaction(IsolationLevel.ReadCommitted)

            // Per-user serialization. pg_advisory_xact_lock(bigint) auto-releases
            // on commit/rollback. Concurrent webhooks for OTHER users don't block.
            // No other code path in the bot uses advisory locks (vahter-bot is a
            // separate DB), so the user_id keyspace is unambiguous.
            let! _ = conn.ExecuteAsync("SELECT pg_advisory_xact_lock(@u)", {| u = userId |}, tx)

            // A new album supersedes any single-photo wizard in progress. Doing
            // the wipe inside this transaction (under the advisory lock) means
            // a concurrent /add command cannot leave its UpsertPendingAddFlow
            // row standing alongside the freshly-created batch — either /add
            // wins the lock first (and album then wipes it) or album wins first
            // (and /add's later AbandonOpenBatchesExcept, also lock-protected,
            // kills the batch the user just abandoned by typing /add).
            let! _ =
                conn.ExecuteAsync(
                    "DELETE FROM pending_add WHERE user_id = @u",
                    {| u = userId |},
                    tx)

            // Housekeeping: reap stale rows (any user) before competing for a new slot.
            //   - 'open' batches > 1h old: the user never finished uploading and we
            //     never finalized. Fair to delete.
            //   - 'awaiting_user' batches > 1h old with bulk_message_id IS NULL:
            //     the bot crashed between TryFlipBatchToAwaiting and the SendMessage
            //     that delivers the bulk-confirm UI. The user has no message to
            //     interact with, so this row leaks forever otherwise — recovery
            //     skips awaiting_user, and only the user uploading another album
            //     would abandon it.
            //language=postgresql
            let reapSql =
                """
DELETE FROM pending_add_batch
WHERE (status = 'open'
       OR (status = 'awaiting_user' AND bulk_message_id IS NULL))
  AND updated_at < (@now_utc - interval '1 hour');
"""
            let! reapedCount = conn.ExecuteAsync(reapSql, {| now_utc = utcNow () |}, tx)

            //language=postgresql
            let insertSql =
                """
INSERT INTO pending_add_batch (user_id, media_group_id, bulk_chat_id)
VALUES (@user_id, @media_group_id, @chat_id)
ON CONFLICT (user_id, media_group_id) WHERE status IN ('open','awaiting_user')
DO NOTHING
RETURNING id;
"""
            let! newId =
                conn.QuerySingleOrDefaultAsync<Nullable<int64>>(
                    insertSql,
                    {| user_id = userId; media_group_id = mediaGroupId; chat_id = chatId |},
                    tx)

            if newId.HasValue then
                // New batch — abandon any other active batches for this user
                // (different media_group_id). The advisory lock guarantees no
                // other webhook for this user is between create and abandon.
                //language=postgresql
                let abandonSql =
                    """
DELETE FROM pending_add_batch
WHERE user_id = @user_id
  AND status IN ('open', 'awaiting_user')
  AND id <> @except_id
RETURNING *;
"""
                let! abandonedRows =
                    conn.QueryAsync<PendingAddBatch>(
                        abandonSql,
                        {| user_id = userId; except_id = newId.Value |},
                        tx)
                let abandoned = abandonedRows |> Seq.toArray
                do! tx.CommitAsync()
                return newId.Value, true, abandoned, reapedCount
            else
                //language=postgresql
                let findSql =
                    """
SELECT id
FROM pending_add_batch
WHERE user_id = @user_id
  AND media_group_id = @media_group_id
  AND status IN ('open', 'awaiting_user')
LIMIT 1;
"""
                let! existingId =
                    conn.QuerySingleAsync<int64>(
                        findSql,
                        {| user_id = userId; media_group_id = mediaGroupId |},
                        tx)
                do! tx.CommitAsync()
                return existingId, false, [||], reapedCount
        }

    /// Deletes all of the user's active batches except `exceptId` (if provided).
    /// Returns the deleted rows so the caller can edit their bulk-confirm messages.
    ///
    /// Takes the per-user advisory lock so this serialises with
    /// CreateBatchAtomically. Without that, a command like `/add` arriving
    /// concurrently with an album webhook can interleave: /add sees no batch
    /// to abandon and inserts pending_add; the album webhook then commits a
    /// fresh batch; user ends up in BOTH flows simultaneously.
    member _.AbandonOpenBatchesExcept(userId: int64, exceptId: int64 option) =
        task {
            use! conn = openConn()
            use tx = conn.BeginTransaction(IsolationLevel.ReadCommitted)
            let! _ = conn.ExecuteAsync("SELECT pg_advisory_xact_lock(@u)", {| u = userId |}, tx)
            //language=postgresql
            let sql =
                """
DELETE FROM pending_add_batch
WHERE user_id = @user_id
  AND status IN ('open', 'awaiting_user')
  AND (@except_id IS NULL OR id <> @except_id)
RETURNING *;
"""
            let exceptParam =
                match exceptId with
                | Some v -> Nullable v
                | None -> Nullable()
            let! rows = conn.QueryAsync<PendingAddBatch>(sql, {| user_id = userId; except_id = exceptParam |}, tx)
            let arr = rows |> Seq.toArray
            do! tx.CommitAsync()
            return arr
        }

    /// Sets bulk_message_id on a batch row. Returns true if the row was found
    /// and updated; false if the row no longer exists (the batch was abandoned
    /// during the SendMessage RPC that produced this messageId). Callers MUST
    /// inspect the result — on false, the just-sent message is an orphan and
    /// should be deleted to avoid a "Получил, обрабатываю купоны..." or
    /// bulk-confirm message lingering in the user's chat with no batch behind it.
    member _.SetBatchBulkMessageId(batchId: int64, messageId: int) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql = "UPDATE pending_add_batch SET bulk_message_id = @msg_id, updated_at = @now_utc WHERE id = @id;"
            let! affected = conn.ExecuteAsync(sql, {| id = batchId; msg_id = messageId; now_utc = utcNow () |})
            return affected > 0
        }

    member _.ClearBatch(batchId: int64) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql = "DELETE FROM pending_add_batch WHERE id = @id;"
            let! _ = conn.ExecuteAsync(sql, {| id = batchId |})
            return ()
        }

    /// Atomic flip 'awaiting_user' -> 'processing' scoped to (batchId, userId).
    /// Returns Some batch if THIS caller won the claim — i.e. it's exclusively
    /// allowed to run BulkBatchConfirm or BulkBatchCancel for this batch.
    /// Returns None if another callback beat us to it (or the batch was already
    /// gone / belongs to a different user / never reached awaiting_user).
    ///
    /// This is what serialises a confirm-click and a cancel-click that arrive
    /// within microseconds of each other from the same user: only one of the
    /// two callback handlers sees a row back; the other answers "уже устарел".
    /// 'processing' is a transient terminal state — BulkBatchConfirm /
    /// BulkBatchCancel always end with ClearBatch (DELETE), so 'processing'
    /// only outlives the winning callback handler if the bot crashes mid-action,
    /// in which case the 1h TTL reaper in CreateBatchAtomically cleans it up.
    member _.TryClaimAwaitingBatch(batchId: int64, userId: int64) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
UPDATE pending_add_batch
SET status = 'processing', updated_at = @now_utc
WHERE id = @id AND user_id = @user_id AND status = 'awaiting_user'
RETURNING *;
"""
            let! row =
                conn.QuerySingleOrDefaultAsync<PendingAddBatch>(
                    sql,
                    {| id = batchId; user_id = userId; now_utc = utcNow () |})
            if obj.ReferenceEquals(row, null) then return None
            else return Some row
        }

    /// Atomic flip 'open' -> 'awaiting_user'. Returns true if THIS caller won the
    /// flip (i.e. it's our job to render the bulk-confirm UI). Returns false if
    /// another finalize handler beat us to it, or the batch was abandoned.
    member _.TryFlipBatchToAwaiting(batchId: int64) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
UPDATE pending_add_batch
SET status = 'awaiting_user', updated_at = @now_utc
WHERE id = @id AND status = 'open'
RETURNING id;
"""
            let! flipped =
                conn.QuerySingleOrDefaultAsync<Nullable<int64>>(
                    sql,
                    {| id = batchId; now_utc = utcNow () |})
            return flipped.HasValue
        }

    /// Atomically reclassify any items still 'pending' at this instant as
    /// 'needs_input' with reason 'timeout'. Returns the count for logging/metrics.
    /// Late OCR writes will no-op because OCR's UPDATE is guarded WHERE status='pending'.
    member _.ClaimPendingItemsAsTimeout(batchId: int64) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
UPDATE pending_add_batch_item
SET status = 'needs_input', failure_note = 'timeout'
WHERE batch_id = @batch_id AND status = 'pending'
RETURNING id;
"""
            let! ids = conn.QueryAsync<int64>(sql, {| batch_id = batchId |})
            return ids |> Seq.length
        }

    /// Inserts a new item under a batch. Locks the batch row to serialize seq
    /// assignment under concurrent webhook arrivals for the same album. Returns
    /// None on duplicate photo_file_id (Telegram redelivery) or on a closed/missing batch.
    member _.AddBatchItem(batchId: int64, photoFileId: string, photoMessageId: int) =
        task {
            use! conn = openConn()
            use tx = conn.BeginTransaction(IsolationLevel.ReadCommitted)

            //language=postgresql
            let lockSql =
                """
SELECT id FROM pending_add_batch
WHERE id = @id AND status IN ('open', 'awaiting_user')
FOR UPDATE;
"""
            let! lockedId =
                conn.QuerySingleOrDefaultAsync<Nullable<int64>>(lockSql, {| id = batchId |}, tx)
            if not lockedId.HasValue then
                do! tx.RollbackAsync()
                return None
            else
                //language=postgresql
                let insertSql =
                    """
INSERT INTO pending_add_batch_item (batch_id, seq, photo_file_id, photo_message_id, status)
SELECT @batch_id,
       COALESCE((SELECT MAX(seq) FROM pending_add_batch_item WHERE batch_id = @batch_id), 0) + 1,
       @photo_file_id,
       @photo_message_id,
       'pending'
ON CONFLICT (batch_id, photo_file_id) DO NOTHING
RETURNING id;
"""
                let! newId =
                    conn.QuerySingleOrDefaultAsync<Nullable<int64>>(
                        insertSql,
                        {| batch_id = batchId
                           photo_file_id = photoFileId
                           photo_message_id = photoMessageId |},
                        tx)
                if newId.HasValue then
                    // Bump batch updated_at so the TTL reaper doesn't pick it up mid-album.
                    //language=postgresql
                    let touchSql = "UPDATE pending_add_batch SET updated_at = @now_utc WHERE id = @id;"
                    let! _ = conn.ExecuteAsync(touchSql, {| id = batchId; now_utc = utcNow () |}, tx)
                    do! tx.CommitAsync()
                    return Some newId.Value
                else
                    do! tx.RollbackAsync()
                    return None
        }

    /// Conditional OCR-success write: only updates if the row is still 'pending'.
    /// Returns true if the update landed (i.e. finalize hasn't already claimed it).
    member _.UpdateBatchItemOcrOk(
            itemId: int64,
            value: Nullable<decimal>,
            minCheck: Nullable<decimal>,
            expiresAt: Nullable<DateOnly>,
            barcodeText: string | null,
            validFrom: Nullable<DateOnly>) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
UPDATE pending_add_batch_item
SET status = 'ok',
    value = @value,
    min_check = @min_check,
    expires_at = @expires_at,
    barcode_text = @barcode_text,
    valid_from = @valid_from
WHERE id = @id AND status = 'pending';
"""
            let! affected =
                conn.ExecuteAsync(
                    sql,
                    {| id = itemId
                       value = value
                       min_check = minCheck
                       expires_at = expiresAt
                       barcode_text = barcodeText
                       valid_from = validFrom |})
            return affected > 0
        }

    member _.UpdateBatchItemNeedsInput(itemId: int64, reason: string) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
UPDATE pending_add_batch_item
SET status = 'needs_input', failure_note = @reason
WHERE id = @id AND status = 'pending';
"""
            let! _ = conn.ExecuteAsync(sql, {| id = itemId; reason = reason |})
            return ()
        }

    member _.MarkBatchItemInserted(itemId: int64, couponId: int) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
UPDATE pending_add_batch_item
SET status = 'inserted', failure_note = @note
WHERE id = @id;
"""
            let! _ = conn.ExecuteAsync(sql, {| id = itemId; note = $"coupon_id={couponId}" |})
            return ()
        }

    member _.MarkBatchItemFailed(itemId: int64, note: string) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
UPDATE pending_add_batch_item
SET status = 'failed', failure_note = @note
WHERE id = @id;
"""
            let! _ = conn.ExecuteAsync(sql, {| id = itemId; note = note |})
            return ()
        }

    member _.GetBatchById(batchId: int64) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql = "SELECT * FROM pending_add_batch WHERE id = @id;"
            let! row = conn.QuerySingleOrDefaultAsync<PendingAddBatch>(sql, {| id = batchId |})
            if obj.ReferenceEquals(row, null) then
                return None
            else
                return Some row
        }

    member _.GetBatchItems(batchId: int64) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql = "SELECT * FROM pending_add_batch_item WHERE batch_id = @id ORDER BY seq;"
            let! rows = conn.QueryAsync<PendingAddBatchItem>(sql, {| id = batchId |})
            return rows |> Seq.toArray
        }

    /// Snapshot of all 'open' batches (incomplete at the time of bot startup).
    /// Used by BatchRecoveryService to re-OCR pending items and re-arm finalize.
    member _.GetOpenBatchesForRecovery() =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql = "SELECT * FROM pending_add_batch WHERE status = 'open' ORDER BY id;"
            let! rows = conn.QueryAsync<PendingAddBatch>(sql)
            return rows |> Seq.toArray
        }
