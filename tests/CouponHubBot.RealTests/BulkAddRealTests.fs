namespace CouponHubBot.RealTests

open System
open System.Globalization
open System.IO
open System.Threading.Tasks
open Dapper
open Npgsql
open Xunit

/// Coverage item 3 (contract): "Bulk/album add (addflow:bulk:confirm) — most stateful
/// path in the bot."
///
/// Uses 3 of the shared EXPIRED-coupon fixtures (same barcode-uniqueness/public-repo
/// constraint as AddFlowRealTests — see its doc comment); their OCR-decoded barcodes
/// (2706688198821 / …838 / …845) are distinct, verified both by reading the fixtures'
/// raw Azure-OCR text and by the passing CouponHubBot.Ocr.Tests theory cases for these
/// exact filenames, so no barcode-uniqueness collision is in play here.
///
/// Unlike the single-photo /add test, an album has no per-photo caption to override
/// OCR's own (past) printed expiry with. CouponFlowHandler.fs's OcrItem gate (does OCR
/// find a barcode + value + min-check + date; see CouponFlowHandler.fs:532-537) has no
/// expiry check, but DbService.TryAddCoupon — called per-item from BulkBatchConfirm —
/// DOES reject `expires_at < todayUtc()` (DbService.fs:198-200), same as the manual /add
/// path. Since every fixture in Images/ is deliberately dated in the past (repo-public
/// constraint above) and the album flow has no caption to carry an override, OCR's own
/// printed expiry would always get every item silently skipped as "Expired" (0 net
/// coupons added — this bit the suite for real: see git blame near this comment).
/// `bumpItemsToFutureExpiry` below overrides just the batch items' `expires_at` via
/// direct SQL after OCR lands (same "direct SQL, real-test-only" pattern
/// ReminderRealTests.fs and AddFlowRealTests' caption both already use to get a
/// real-but-photographed-expired fixture past TryAddCoupon's expiry gate) so the
/// pipeline being exercised — album -> debounce -> awaiting_user -> bulk-confirm -> DB
/// insert — completes for real and actually lands 3 coupons, which is what "most
/// stateful path in the bot" and this test's own name both claim.
///
/// BATCH_DEBOUNCE_MS is seeded to 1000ms (contract's bot_setting table) specifically so
/// this test doesn't idle on the production 5s default — but it's still scheduled
/// through CouponHubBot's FakeTimeProvider (BatchDebounce.fs), which only advances via
/// POST /test/clock/advance, never real wall-clock. See DbSeed.fs's doc comment for the
/// prerequisite that TEST_MODE=true must already be true when the bot process itself
/// booted for /test/clock/advance to work at all.
type BulkAddRealTests(fx: RealAssemblyFixture) =

    let images =
        [ "10_50_2026-01-17_2026-01-26_2706688198821.jpg"
          "10_50_2026-01-17_2026-01-26_2706688198838.jpg"
          "10_50_2026-01-17_2026-01-26_2706688198845.jpg" ]
        |> List.map (fun f -> Path.Combine(RealEnv.ocrFixtureImagesDir, f))

    let pollInterval = TimeSpan.FromMilliseconds 500.

    let waitFor (description: string) (timeout: TimeSpan) (check: unit -> Task<'a option>) =
        task {
            let deadline = DateTime.UtcNow + timeout
            let mutable result = None
            while result.IsNone && DateTime.UtcNow < deadline do
                let! r = check ()
                result <- r
                if result.IsNone then do! Task.Delay pollInterval
            match result with
            | Some v -> return v
            | None -> return raise (AwaitTimeoutException $"Timed out waiting for {description} after {timeout.TotalSeconds}s")
        }

    let waitForBatchByOwner (ownerId: int64) =
        waitFor "pending_add_batch row" (TimeSpan.FromSeconds 15.) (fun () ->
            task {
                use conn = new NpgsqlConnection(fx.DbConnectionString)
                let! id =
                    conn.QuerySingleAsync<int64>(
                        "SELECT COALESCE((SELECT id FROM pending_add_batch WHERE user_id=@u ORDER BY id DESC LIMIT 1), 0)",
                        {| u = ownerId |})
                return if id = 0L then None else Some id
            })

    let waitForAllItemsTerminal (batchId: int64) =
        waitFor "batch items to leave 'pending'" (TimeSpan.FromSeconds 60.) (fun () ->
            task {
                use conn = new NpgsqlConnection(fx.DbConnectionString)
                let! pendingCount =
                    conn.QuerySingleAsync<int64>(
                        "SELECT COUNT(*)::bigint FROM pending_add_batch_item WHERE batch_id=@b AND status='pending'",
                        {| b = batchId |})
                return if pendingCount = 0L then Some () else None
            })

    let waitForBatchStatus (batchId: int64) (expected: string) =
        waitFor $"batch {batchId} status='{expected}'" (TimeSpan.FromSeconds 30.) (fun () ->
            task {
                use conn = new NpgsqlConnection(fx.DbConnectionString)
                let! status =
                    conn.QuerySingleAsync<string>(
                        "SELECT COALESCE((SELECT status FROM pending_add_batch WHERE id=@b), '__GONE__')",
                        {| b = batchId |})
                return if status = expected then Some () else None
            })

    let waitForBatchCleared (batchId: int64) =
        waitFor $"batch {batchId} cleared" (TimeSpan.FromSeconds 30.) (fun () ->
            task {
                use conn = new NpgsqlConnection(fx.DbConnectionString)
                let! count = conn.QuerySingleAsync<int64>("SELECT COUNT(*)::bigint FROM pending_add_batch WHERE id=@b", {| b = batchId |})
                return if count = 0L then Some () else None
            })

    let countCouponsForOwner (ownerId: int64) =
        task {
            use conn = new NpgsqlConnection(fx.DbConnectionString)
            return! conn.QuerySingleAsync<int64>("SELECT COUNT(*)::bigint FROM coupon WHERE owner_id=@u", {| u = ownerId |})
        }

    /// The album flow has no caption to carry an expiry override, so — mirroring
    /// AddFlowRealTests' explicit-future-caption trick via direct SQL instead — bump the
    /// OCR-landed 'ok' items' expires_at past DbService.TryAddCoupon's `expires_at <
    /// todayUtc()` gate (DbService.fs:198-200) before the batch is confirmed. Leaves
    /// value/min_check/barcode_text (the actually-under-test OCR output) untouched.
    let bumpItemsToFutureExpiry (batchId: int64) =
        task {
            let futureExpiry = DateTime.UtcNow.AddDays(400.).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            use conn = new NpgsqlConnection(fx.DbConnectionString)
            let! _ =
                conn.ExecuteAsync(
                    "UPDATE pending_add_batch_item SET expires_at=@e::date WHERE batch_id=@b AND status='ok'",
                    {| b = batchId; e = futureExpiry |})
            return ()
        }

    [<Fact>]
    member _.``album of 3 photos finalizes into a bulk-confirm; confirming adds 3 coupons``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()
            images |> List.iter (fun p -> Assert.True(File.Exists p, $"Expired-coupon fixture missing: {p}"))

            let ownerId = fx.UserClient.Me.id
            let! countBefore = countCouponsForOwner ownerId

            let! lastAlbumMsgId = fx.UserClient.SendAlbum(fx.BotChatId, images)

            let! batchId = waitForBatchByOwner ownerId
            do! waitForAllItemsTerminal batchId
            do! bumpItemsToFutureExpiry batchId
            do! fx.AdvanceClockAsync 2000 // BATCH_DEBOUNCE_MS=1000 (contract seed) + margin
            do! waitForBatchStatus batchId "awaiting_user"

            let! confirmMsg = fx.UserClient.AwaitTextContaining(fx.BotChatId, lastAlbumMsgId, "Подтвердить", TimeSpan.FromSeconds 30.)
            let callbackData =
                fx.UserClient.FindCallbackData(confirmMsg, fun d -> d.StartsWith "addflow:bulk:confirm:")
            Assert.True(callbackData.IsSome, "Expected an addflow:bulk:confirm:<batchId> button on the bulk-confirm message")

            do! fx.UserClient.PressCallbackButton(fx.BotChatId, confirmMsg.id, callbackData.Value)
            do! waitForBatchCleared batchId

            let! countAfter = countCouponsForOwner ownerId
            Assert.Equal(countBefore + 3L, countAfter)
        })
