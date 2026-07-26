namespace CouponHubBot.RealTests

open System
open System.Globalization
open Dapper
open Npgsql
open Xunit

/// Coverage item 10 (contract): "Reminder: POST /test/run-reminder -> overdue DM
/// arrives in real Telegram."
///
/// Coupon creation here is a direct SQL insert rather than a real /add — the overdue-
/// taken reminder only cares about a 'taken' coupon with a backdated taken_at, and
/// items 2/3 already cover the real OCR /add path; reusing it here would just spend
/// another real Azure OCR call and another fixture barcode for no extra coverage.
type ReminderRealTests(fx: RealAssemblyFixture) =

    [<Fact>]
    member _.``run-reminder sends a real overdue-taken DM``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let me = fx.UserClient.Me
            let username = if String.IsNullOrWhiteSpace me.username then "coupon_real_test_user" else me.username
            let futureExpiry = DateTime.UtcNow.AddDays(200.).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            // Overdue-taken nag requires expires_at still in the future (a bug fixed by
            // PR #263 — see git log "overdue-taken-coupon reminder DM nags about expired
            // coupons forever" — an already-expired taken coupon must NOT nag).
            let takenAt = DateTime.UtcNow.AddDays(-3.)

            use conn = new NpgsqlConnection(fx.DbConnectionString)
            do! conn.OpenAsync()

            let! _ =
                conn.ExecuteAsync(
                    """
INSERT INTO "user"(id, username, first_name, created_at, updated_at)
VALUES (@id, @username, @username, NOW(), NOW())
ON CONFLICT (id) DO NOTHING;
""",
                    {| id = me.id; username = username |})

            let! couponId =
                conn.QuerySingleAsync<int>(
                    """
INSERT INTO coupon(owner_id, photo_file_id, value, min_check, expires_at, status, taken_by, taken_at)
VALUES (@owner_id, @photo, 10.00, 50.00, @expires_at::date, 'taken', @owner_id, @taken_at::timestamptz)
RETURNING id;
""",
                    {| owner_id = me.id
                       photo = $"real-test-reminder-{Guid.NewGuid():N}"
                       expires_at = futureExpiry
                       taken_at = takenAt.ToString("o", CultureInfo.InvariantCulture) |})

            // Cleanup is mandatory, not cosmetic: this coupon's `photo_file_id` is a
            // synthetic GUID string, never a real Telegram file id, and its `barcode_text`
            // is NULL (deleteCouponsByBarcodeAsync can never find/remove it by barcode).
            // Left behind 'taken' for the rest of this serial run, it (a) permanently eats
            // one of this account's MAX_TAKEN_COUPONS=6 slots, and (b) gets included in
            // every later /my's photo/media-group send (CommandHandler.fs:196-199,
            // `GetCouponsTakenBy` has no way to exclude it) — Telegram rejects that whole
            // request with "wrong remote file identifier specified: Wrong padding in the
            // string", crashing the /my handler for every later test in the run
            // (confirmed against run 30181924500's bot pod logs, UpdateId 84308640/84308644).
            // F#'s task CE can't `do!`/`let!` inside a `finally` block, hence try/with +
            // explicit re-raise (same shape AdminAndFeedbackRealTests.fs's "short aliases"
            // test already uses for the same reason).
            try
                // Fresh baseline in this long-lived private DM's message-id sequence — the
                // reminder is triggered over plain HTTP (no MTProto send of our own), so
                // there is no naturally fresh "afterMsgId" otherwise (see TgUserClient.fs's
                // doc comment on why AfterMsgId-gating exists at all).
                let! baselineId = fx.UserClient.SendText(fx.BotChatId, "/help")
                let! _helpReply = fx.UserClient.AwaitTextContaining(fx.BotChatId, baselineId, "Команды", TimeSpan.FromSeconds 30.)

                do! fx.RunReminderAsync(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))

                let! dm = fx.UserClient.AwaitTextContaining(fx.BotChatId, baselineId, $"ID:{couponId}", TimeSpan.FromSeconds 60.)
                Assert.Contains("Напоминание", dm.message)
                do! DbSeed.deleteCouponByIdAsync fx.DbConnectionString couponId
            with ex ->
                do! DbSeed.deleteCouponByIdAsync fx.DbConnectionString couponId
                raise ex
        })
