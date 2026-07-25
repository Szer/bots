namespace CouponHubBot.Tests

open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open BotTestInfra
open Dapper
open Npgsql
open Xunit
open FakeCallHelpers

/// Local row shape for the §8 bucket-reconciliation check (test 10): the four outcome buckets
/// plus the new 'reported' bucket must sum to total_count, computed in one round trip.
[<CLIMutable>]
type private BucketCheckRow = { sum_buckets: int64; total_count: int64 }

type ReportFlowTests(fixture: DefaultCouponHubTestContainers) =

    let adminId = 900L

    let getLatestCouponId () =
        task {
            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            return! conn.QuerySingleAsync<int>("SELECT id FROM coupon ORDER BY id DESC LIMIT 1")
        }

    let getCouponStatus (couponId: int) =
        fixture.QuerySingle<string>("SELECT status FROM coupon WHERE id = @id", {| id = couponId |})

    // 0 when taken_by is NULL.
    let getTakenBy (couponId: int) =
        fixture.QuerySingle<int64>("SELECT COALESCE(taken_by, 0) FROM coupon WHERE id = @id", {| id = couponId |})

    let getEventCount (couponId: int) (eventType: string) =
        fixture.QuerySingle<int64>(
            "SELECT COUNT(*)::bigint FROM coupon_event WHERE coupon_id = @id AND event_type = @t",
            {| id = couponId; t = eventType |})

    let getEventCountByUser (couponId: int) (eventType: string) (userId: int64) =
        fixture.QuerySingle<int64>(
            "SELECT COUNT(*)::bigint FROM coupon_event WHERE coupon_id = @id AND event_type = @t AND user_id = @u",
            {| id = couponId; t = eventType; u = userId |})

    let runReminderAt (nowUtc: string) =
        task {
            use body = new StringContent("", Encoding.UTF8, "application/json")
            let! resp = fixture.Bot.PostAsync($"/test/run-reminder?nowUtc={nowUtc}", body)
            if not resp.IsSuccessStatusCode then
                let! text = resp.Content.ReadAsStringAsync()
                failwith $"Expected 2xx from /test/run-reminder, got {resp.StatusCode}. Body: {text}"
        }

    let tryGetText (body: string) =
        try
            use doc = JsonDocument.Parse(body)
            match doc.RootElement.TryGetProperty("text") with
            | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
            | _ -> None
        with _ -> None

    // Finds the leaderboard line naming `handle` (e.g. "@ivanov") within the all-time-stats post.
    let findLeaderboardLine (calls: FakeCall array) (handle: string) =
        calls
        |> Array.choose (fun c -> tryGetText c.Body)
        |> Array.tryFind (fun t -> t.Contains("Статистика за всё время"))
        |> Option.bind (fun t ->
            t.Split('\n') |> Array.tryFind (fun line -> line.Contains(handle)))

    // ── 1. /report command ──────────────────────────────────────────────────

    [<Fact>]
    let ``Holder reports coupon via /report command`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 9715L, username = "report_cmd_owner", firstName = "Owner")
            let taker = Tg.user(id = 9716L, username = "report_cmd_taker", firstName = "Taker")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage($"/report {couponId}", taker))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls taker.Id "отправлен владельцу",
                "Expected reporter confirmation")

            let! status = getCouponStatus couponId
            Assert.Equal("reported", status)
            let! takenBy = getTakenBy couponId
            Assert.Equal(0L, takenBy)
            let! reportedCount = getEventCountByUser couponId "reported" taker.Id
            Assert.Equal(1L, reportedCount)
        }

    // ── 2. Button flow: report -> report:<id> -> report:<id>:confirm ───────

    [<Fact>]
    let ``Holder reports coupon via button flow`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 9717L, username = "report_btn_owner", firstName = "Owner")
            let taker = Tg.user(id = 9718L, username = "report_btn_taker", firstName = "Taker")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))

            do! fixture.ClearFakeCalls()
            let! resp1 = fixture.SendUpdate(Tg.dmCallback("report", taker))
            Assert.Equal(HttpStatusCode.OK, resp1.StatusCode)
            let! calls1 = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls1 taker.Id "уже использован",
                "Expected coupon-selection prompt")
            Assert.True(calls1 |> Array.exists (fun c -> c.Body.Contains($"report:{couponId}")),
                "Expected a selection button for the held coupon")

            do! fixture.ClearFakeCalls()
            let! resp2 = fixture.SendUpdate(Tg.dmCallback($"report:{couponId}", taker))
            Assert.Equal(HttpStatusCode.OK, resp2.StatusCode)
            let! calls2 = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls2 taker.Id "Подтвердить?",
                "Expected a confirm-before-report prompt")
            Assert.True(findCallWithText calls2 taker.Id "@report_btn_owner",
                "Expected the confirm prompt to name the owner")

            do! fixture.ClearFakeCalls()
            let! resp3 = fixture.SendUpdate(Tg.dmCallback($"report:{couponId}:confirm", taker))
            Assert.Equal(HttpStatusCode.OK, resp3.StatusCode)
            let! calls3 = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls3 taker.Id "отправлен владельцу",
                "Expected final report confirmation")

            let! status = getCouponStatus couponId
            Assert.Equal("reported", status)
        }

    // ── 3. Non-holder cannot report ─────────────────────────────────────────

    [<Fact>]
    let ``Non-holder cannot report a coupon`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 9719L, username = "report_nh_owner", firstName = "Owner")
            let taker = Tg.user(id = 9720L, username = "report_nh_taker", firstName = "Taker")
            let stranger = Tg.user(id = 9721L, username = "report_nh_stranger", firstName = "Stranger")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")
            do! fixture.SetChatMemberStatus(stranger.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage($"/report {couponId}", stranger))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls stranger.Id "не у тебя",
                "Expected a non-holder rejection")

            let! status = getCouponStatus couponId
            Assert.Equal("taken", status)
        }

    // ── 4. Reported coupon absent from /list ────────────────────────────────

    [<Fact>]
    let ``Reported coupon is absent from /list`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 9722L, username = "report_list_owner", firstName = "Owner")
            let taker = Tg.user(id = 9723L, username = "report_list_taker", firstName = "Taker")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/report {couponId}", taker))

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage("/list", owner))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls owner.Id "Всего доступно купонов: 0",
                "Expected zero available coupons after report")
            Assert.False(calls |> Array.exists (fun c -> c.Body.Contains($"ID:{couponId}")),
                "Reported coupon must not appear in /list")
        }

    // ── 5. Reported coupon in adder's /my: Использован, never Вернуть ──────

    [<Fact>]
    let ``Reported coupon appears in adder's my with Использован and no Вернуть`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 9724L, username = "report_my_owner", firstName = "Owner")
            let taker = Tg.user(id = 9725L, username = "report_my_taker", firstName = "Taker")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/report {couponId}", taker))

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage("/my", owner))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls owner.Id "использованные вне бота",
                "Expected the reported-coupons section header in /my")
            Assert.True(calls |> Array.exists (fun c -> c.Body.Contains($"reportedUsed:{couponId}")),
                "Expected an Использован button for the reported coupon")
            Assert.False(calls |> Array.exists (fun c -> c.Body.Contains($"return:{couponId}")),
                "A reported coupon must never offer a Вернуть button (returning a known-dead coupon feeds the next victim)")
        }

    // ── 6. Adder marks reported coupon used ─────────────────────────────────

    [<Fact>]
    let ``Adder marks reported coupon used via reportedUsed callback`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 9726L, username = "report_used_owner", firstName = "Owner")
            let taker = Tg.user(id = 9727L, username = "report_used_taker", firstName = "Taker")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/report {couponId}", taker))

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmCallback($"reportedUsed:{couponId}", owner))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls owner.Id "отмечен как использованный",
                "Expected confirmation that the coupon was marked used")

            let! status = getCouponStatus couponId
            Assert.Equal("used", status)
            let! usedCount = getEventCountByUser couponId "used" owner.Id
            Assert.Equal(1L, usedCount)
        }

    // ── 7. Adder can /void a reported coupon from /added ────────────────────

    [<Fact>]
    let ``Adder can void a reported coupon from added`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 9728L, username = "report_void_owner", firstName = "Owner")
            let taker = Tg.user(id = 9729L, username = "report_void_taker", firstName = "Taker")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/report {couponId}", taker))

            do! fixture.ClearFakeCalls()
            let! addedResp = fixture.SendUpdate(Tg.dmMessage("/added", owner))
            Assert.Equal(HttpStatusCode.OK, addedResp.StatusCode)
            let! addedCalls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText addedCalls owner.Id "(отмечен использованным)",
                "Expected the reported-status suffix in /added output")
            Assert.True(addedCalls |> Array.exists (fun c -> c.Body.Contains($"void:{couponId}")),
                "Expected an Аннулировать button for the reported coupon in /added")

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage($"/void {couponId}", owner))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls owner.Id "аннулирован", "Expected void confirmation")

            let! status = getCouponStatus couponId
            Assert.Equal("voided", status)
        }

    // ── 8. Adder receives DM naming the reporter and coupon id ─────────────

    [<Fact>]
    let ``Adder receives DM naming the reporter and the coupon id`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 9730L, username = "report_dm_owner", firstName = "Owner")
            let taker = Tg.user(id = 9731L, username = "report_dm_taker", firstName = "Taker")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage($"/report {couponId}", taker))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls owner.Id "@report_dm_taker",
                "Expected the adder DM to name the reporter")
            Assert.True(findCallWithText calls owner.Id $"ID:{couponId}",
                "Expected the adder DM to name the coupon id")
        }

    // ── 9. Reported coupon does not trigger the overdue-taken nag ──────────

    [<Fact>]
    let ``Reported coupon does not trigger the overdue-taken nag`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 9732L, username = "report_nag_owner", firstName = "Owner")
            let taker = Tg.user(id = 9733L, username = "report_nag_taker", firstName = "Taker")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-07-30", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))

            // Backdate taken_at so this coupon would be "overdue" (>1 day) if it were still 'taken'.
            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            let! _ =
                conn.ExecuteAsync(
                    "UPDATE coupon SET taken_at = '2026-07-22T18:25:17Z'::timestamptz WHERE id = @id",
                    {| id = couponId |})

            let! _ = fixture.SendUpdate(Tg.dmMessage($"/report {couponId}", taker))
            let! status = getCouponStatus couponId
            Assert.Equal("reported", status)

            do! fixture.ClearFakeCalls()
            do! runReminderAt "2026-07-24T09:00:00Z"

            let! calls = fixture.GetFakeCalls("sendMessage")
            let dmCallsToTaker =
                calls
                |> Array.filter (fun c ->
                    match parseCallBody c.Body with
                    | Some p -> p.ChatId = Some taker.Id
                    | _ -> false)
            Assert.Equal(0, dmCallsToTaker.Length)
        }

    // ── 10. /stats reported bucket increments; bucket sum reconciles ───────

    [<Fact>]
    let ``Stats reported bucket increments and bucket sum reconciles with total_count`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            let userId = 9734L
            let! _ =
                conn.ExecuteAsync(
                    """
INSERT INTO "user"(id, username, first_name, created_at, updated_at)
VALUES (@id, @username, @username, NOW(), NOW())
ON CONFLICT (id) DO NOTHING;
""",
                    {| id = userId; username = "stats_report_user" |})

            do!
                conn.ExecuteAsync(
                    """
INSERT INTO coupon(owner_id, photo_file_id, value, min_check, expires_at, status)
VALUES
  (9734, 'rep-p1', 10, 50, '2025-06-01', 'used'),
  (9734, 'rep-p2', 10, 50, '2025-06-01', 'available'),
  (9734, 'rep-p3', 10, 50, '2026-06-01', 'available'),
  (9734, 'rep-p4', 10, 50, '2026-06-01', 'voided'),
  (9734, 'rep-p5', 10, 50, '2026-06-01', 'reported');
"""
                )
                :> Task

            let user = Tg.user(id = userId, username = "stats_report_user", firstName = "StatsReport")
            do! fixture.SetChatMemberStatus(user.Id, "member")
            let! resp = fixture.SendUpdate(Tg.dmMessage("/stats", user))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls userId "Отмечено использованными вне бота: 1",
                "Expected the reported bucket = 1 in the personal section")

            //language=postgresql
            let bucketSql =
                """
SELECT
    (COUNT(*) FILTER (WHERE status = 'used')
   + COUNT(*) FILTER (WHERE status IN ('available','taken') AND expires_at < @today::date)
   + COUNT(*) FILTER (WHERE status IN ('available','taken') AND expires_at >= @today::date)
   + COUNT(*) FILTER (WHERE status = 'voided')
   + COUNT(*) FILTER (WHERE status = 'reported'))::bigint AS sum_buckets,
    COUNT(*)::bigint AS total_count
FROM coupon
WHERE owner_id = @user_id;
"""
            let! row =
                conn.QuerySingleAsync<BucketCheckRow>(
                    bucketSql,
                    {| today = fixture.FixedToday.ToString("yyyy-MM-dd"); user_id = userId |})
            Assert.Equal(row.total_count, row.sum_buckets)
        }

    // ── 11. Admin /undo of a report restores taken + original taken_by ─────

    [<Fact>]
    let ``Admin undo of a report restores taken status and original taken_by`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 9735L, username = "report_undo_owner", firstName = "Owner")
            let taker = Tg.user(id = 9736L, username = "report_undo_taker", firstName = "Taker")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")
            do! fixture.SetChatMemberStatus(adminId, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/report {couponId}", taker))

            let! status1 = getCouponStatus couponId
            Assert.Equal("reported", status1)

            let! _ = fixture.SendUpdate(Tg.dmMessage($"/undo {couponId}", Tg.user(id = adminId, username = "admin", firstName = "Admin")))

            let! status2 = getCouponStatus couponId
            Assert.Equal("taken", status2)
            let! takenBy = getTakenBy couponId
            Assert.Equal(taker.Id, takenBy)
            let! revCount = getEventCount couponId "reported_reverted"
            Assert.Equal(1L, revCount)
        }

    // ── 12. Barcode uniqueness holds while a reported coupon owns the slot ─

    [<Fact>]
    let ``Same barcode cannot be re-added while a reported coupon holds that slot`` () =
        task {
            do! fixture.TruncateCoupons()

            let barcode = "9999999999999"
            let expiresAt = "2026-06-01"

            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            do!
                conn.ExecuteAsync(
                    """
INSERT INTO "user"(id, username, first_name, created_at, updated_at)
VALUES (9737, 'bc_owner', 'BcOwner', NOW(), NOW())
ON CONFLICT (id) DO NOTHING;

INSERT INTO coupon(owner_id, photo_file_id, value, min_check, expires_at, barcode_text, status)
VALUES (9737, 'bc-photo-1', 10, 50, @expires_at::date, @barcode, 'reported');
""",
                    {| expires_at = expiresAt; barcode = barcode |})
                :> Task

            let insertSql =
                """
INSERT INTO coupon(owner_id, photo_file_id, value, min_check, expires_at, barcode_text, status)
VALUES (9737, 'bc-photo-2', 10, 50, @expires_at::date, @barcode, 'available');
"""
            let! ex =
                Assert.ThrowsAsync<PostgresException>(fun () ->
                    fixture.Execute(insertSql, {| expires_at = expiresAt; barcode = barcode |}) :> Task)
            Assert.Equal("23505", ex.SqlState)
        }

    // ── 13. Leaderboard attributes the report to the owner, not the reporter (§8a inversion trap) ─

    [<Fact>]
    let ``Monthly leaderboard attributes the report to the coupon's owner, not the reporter`` () =
        task {
            do! fixture.ClearFakeCalls()

            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            do!
                conn.ExecuteAsync(
                    """
INSERT INTO "user"(id, username, first_name, created_at, updated_at)
VALUES (9600,'owner_a','OwnerA',NOW(),NOW()),
       (9601,'reporter_b','ReporterB',NOW(),NOW())
ON CONFLICT (id) DO NOTHING;

INSERT INTO coupon(id, owner_id, photo_file_id, value, min_check, expires_at, status)
VALUES
  (96001,9600,'p-96001',10.00,50.00,'2026-02-01','reported'),
  (96002,9601,'p-96002',10.00,50.00,'2026-02-01','available')
ON CONFLICT (id) DO NOTHING;

-- 'added' events give both A and B a leaderboard line; the 'reported' event is filed
-- BY B (user_id = 9601, the reporter) but ON A's coupon (owner_id = 9600) — this is the
-- inversion trap: a naive GetUserEventCounts("reported") would key it to B, not A.
INSERT INTO coupon_event(coupon_id, user_id, event_type, created_at)
VALUES
  (96001,9600,'added','2026-01-18T10:00:00Z'),
  (96002,9601,'added','2026-01-18T10:05:00Z'),
  (96001,9601,'taken','2026-01-18T11:00:00Z'),
  (96001,9601,'reported','2026-01-18T12:00:00Z');
"""
                )
                :> Task

            // 2026-02-02 is the first Monday of February 2026.
            do! runReminderAt "2026-02-02T10:00:00Z"

            let! calls = fixture.GetFakeCalls("sendMessage")
            let ownerLine = findLeaderboardLine calls "@owner_a"
            Assert.True(ownerLine.IsSome, "Expected owner_a to have a leaderboard line")
            Assert.Contains("⚠️1", ownerLine.Value)

            let reporterLine = findLeaderboardLine calls "@reporter_b"
            match reporterLine with
            | Some line -> Assert.DoesNotContain("⚠️", line)
            | None -> ()
        }

    // ── 14. Admin-undone report no longer counts in the leaderboard ────────

    [<Fact>]
    let ``Admin-undone report no longer counts in the monthly leaderboard`` () =
        task {
            do! fixture.ClearFakeCalls()

            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            do!
                conn.ExecuteAsync(
                    """
INSERT INTO "user"(id, username, first_name, created_at, updated_at)
VALUES (9602,'owner_c','OwnerC',NOW(),NOW()),
       (9603,'reporter_d','ReporterD',NOW(),NOW())
ON CONFLICT (id) DO NOTHING;

INSERT INTO coupon(id, owner_id, photo_file_id, value, min_check, expires_at, status)
VALUES
  (96003,9602,'p-96003',10.00,50.00,'2026-02-01','taken'),
  (96004,9603,'p-96004',10.00,50.00,'2026-02-01','available')
ON CONFLICT (id) DO NOTHING;

-- Same shape as test 13, but the report was admin-undone: 'reported_reverted' nets it to zero.
INSERT INTO coupon_event(coupon_id, user_id, event_type, created_at)
VALUES
  (96003,9602,'added','2026-01-18T10:00:00Z'),
  (96004,9603,'added','2026-01-18T10:05:00Z'),
  (96003,9603,'taken','2026-01-18T11:00:00Z'),
  (96003,9603,'reported','2026-01-18T12:00:00Z'),
  (96003,9603,'reported_reverted','2026-01-18T13:00:00Z');
"""
                )
                :> Task

            do! runReminderAt "2026-02-02T10:00:00Z"

            let! calls = fixture.GetFakeCalls("sendMessage")
            let ownerLine = findLeaderboardLine calls "@owner_c"
            match ownerLine with
            | Some line -> Assert.DoesNotContain("⚠️", line)
            | None -> ()
        }
