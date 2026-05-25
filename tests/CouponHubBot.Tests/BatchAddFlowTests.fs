namespace CouponHubBot.Tests

open BotTestInfra
open System
open System.IO
open System.Net
open System.Text.Json
open System.Threading.Tasks
open Xunit
open FakeCallHelpers
open BatchTestHelpers

/// Album-batch happy path + OCR pipeline (retry, in-flight, fast-finalize) tests.
///
/// In TestMode the bot uses FakeTimeProvider; the debounce timer in
/// BatchDebounce is scheduled through it, so the timer never fires on real
/// wall clock during a test. Each test composes a deterministic sequence:
///
///   send photo(s) → poll until items reach the expected state
///                 → fixture.AdvanceBotClock to fire finalize
///                 → poll until batch reaches awaiting_user (or is cleared)
///                 → assert
type BatchAddFlowTests(fixture: OcrCouponHubTestContainers) =

    let getCouponCount () =
        fixture.QuerySingle<int64>("SELECT COUNT(*)::bigint FROM coupon", null)

    let getBatchCount () =
        fixture.QuerySingle<int64>("SELECT COUNT(*)::bigint FROM pending_add_batch", null)

    let setupBatchTest () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            do! fixture.TruncateBatches()
            do! resetOcrFakes fixture
        }

    // ── Happy path ──────────────────────────────────────────────────────

    [<Fact>]
    let ``Album of 3 OK photos: one placeholder, one bulk-confirm, confirm adds 3 coupons`` () =
        task {
            do! setupBatchTest ()

            let user = Tg.user(id = 7001L, username = "album_happy", firstName = "Album")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-happy-{DateTime.UtcNow.Ticks}"

            let files = [
                "album-happy-1", "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
                "album-happy-2", "10_50_2026-01-17_2026-01-26_2706688198838.jpg"
                "album-happy-3", "10_50_2026-01-17_2026-01-26_2706688198821.jpg"
            ]
            for fid, fn in files do
                do! fixture.SetTelegramFile(fid, readImageBytes fn)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson (snd files[0]))

            for fid, _ in files do
                let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = fid))
                ()

            let! batchId = waitForBatchByUser fixture user.Id 5000
            do! waitForAllItemsTerminal fixture batchId 10000

            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchId "awaiting_user" 5000
            do! waitForBulkConfirmCall fixture user.Id 5000

            let! calls = fixture.GetFakeCalls("sendMessage")
            let placeholders = placeholderCalls calls user.Id
            let bulkConfirms = bulkConfirmCalls calls user.Id

            Assert.Equal(1, placeholders.Length)
            Assert.Equal(1, bulkConfirms.Length)

            let! deletes = fixture.GetFakeCalls("deleteMessage")
            Assert.True(deletes.Length >= 1, $"Expected ≥1 deleteMessage; got {deletes.Length}")

            Assert.True(
                findCallWithText calls user.Id "Подтвердить 3 купона",
                "Expected bulk-confirm text 'Подтвердить 3 купона'")

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback($"addflow:bulk:confirm:{batchId}", user))
            do! waitForBatchCleared fixture batchId 5000

            let! count = getCouponCount ()
            Assert.Equal(3L, count)
            let! batches = getBatchCount ()
            Assert.Equal(0L, batches)
        }

    [<Fact>]
    let ``Album of 1 photo: batch path still works, confirm adds 1`` () =
        task {
            do! setupBatchTest ()

            let user = Tg.user(id = 7002L, username = "album_one", firstName = "Solo")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-one-{DateTime.UtcNow.Ticks}"
            let fid = "album-one-1"
            let fn = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            do! setupGoodOcr fixture fid fn

            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = fid))

            let! batchId = waitForBatchByUser fixture user.Id 5000
            do! waitForAllItemsTerminal fixture batchId 10000

            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchId "awaiting_user" 5000
            do! waitForBulkConfirmCall fixture user.Id 5000

            let! calls = fixture.GetFakeCalls("sendMessage")
            let bulkConfirms = bulkConfirmCalls calls user.Id
            Assert.Equal(1, bulkConfirms.Length)
            Assert.True(
                findCallWithText calls user.Id "Подтвердить 1",
                "Expected bulk-confirm text 'Подтвердить 1'")

            let! _ = fixture.SendUpdate(Tg.dmCallback($"addflow:bulk:confirm:{batchId}", user))
            do! waitForBatchCleared fixture batchId 5000

            let! count = fixture.QuerySingle<int64>("SELECT COUNT(*)::bigint FROM coupon WHERE owner_id=@u", {| u = user.Id |})
            Assert.Equal(1L, count)
        }

    [<Fact>]
    let ``Album of 5 photos (production burst size): all 5 inserted on confirm`` () =
        task {
            do! setupBatchTest ()

            let user = Tg.user(id = 7003L, username = "album_five", firstName = "Five")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-five-{DateTime.UtcNow.Ticks}"
            // Use 5 distinct images so ZXing decodes 5 unique barcodes.
            let files = [
                "five-1", "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
                "five-2", "10_50_2026-01-17_2026-01-26_2706688198838.jpg"
                "five-3", "10_50_2026-01-17_2026-01-26_2706688198821.jpg"
                "five-4", "10_50_2026-01-11_2026-01-20_2706678568818.jpg"
                "five-5", "10_50_01-12_01-21_2706513420233.jpg"
            ]
            for fid, fn in files do
                do! fixture.SetTelegramFile(fid, readImageBytes fn)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson (snd files[0]))

            for fid, _ in files do
                let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = fid))
                ()

            let! batchId = waitForBatchByUser fixture user.Id 5000
            do! waitForAllItemsTerminal fixture batchId 15000

            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchId "awaiting_user" 5000
            do! waitForBulkConfirmCall fixture user.Id 5000

            let! calls = fixture.GetFakeCalls("sendMessage")
            // Exactly one placeholder and one fresh bulk-confirm regardless of N.
            Assert.Equal(1, (placeholderCalls calls user.Id).Length)
            Assert.Equal(1, (bulkConfirmCalls calls user.Id).Length)
            Assert.True(findCallWithText calls user.Id "Подтвердить 5 купонов",
                        "Expected bulk-confirm text 'Подтвердить 5 купонов'")

            let! _ = fixture.SendUpdate(Tg.dmCallback($"addflow:bulk:confirm:{batchId}", user))
            do! waitForBatchCleared fixture batchId 5000

            let! count = fixture.QuerySingle<int64>("SELECT COUNT(*)::bigint FROM coupon WHERE owner_id=@u", {| u = user.Id |})
            Assert.Equal(5L, count)
        }

    [<Fact>]
    let ``Placeholder deleteMessage failure: bulk-confirm still goes out`` () =
        task {
            do! setupBatchTest ()

            let user = Tg.user(id = 7004L, username = "del_fail", firstName = "DelFail")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-delfail-{DateTime.UtcNow.Ticks}"
            let fid = "delfail-1"
            let fn = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            do! setupGoodOcr fixture fid fn

            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = fid))
            let! batchId = waitForBatchByUser fixture user.Id 5000
            do! waitForAllItemsTerminal fixture batchId 10000

            // Make the fake-tg api return an error for deleteMessage. The bot's
            // try/with around DeleteMessage should swallow the error and still
            // send the fresh bulk-confirm.
            do! fixture.SetMethodError("deleteMessage", true)

            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchId "awaiting_user" 5000
            do! waitForBulkConfirmCall fixture user.Id 5000

            // Even though deleteMessage failed, the bulk-confirm landed.
            let! calls = fixture.GetFakeCalls("sendMessage")
            let bulkConfirms = bulkConfirmCalls calls user.Id
            Assert.Equal(1, bulkConfirms.Length)
            Assert.True(findCallWithText calls user.Id "Подтвердить 1",
                        "Expected bulk-confirm text after deleteMessage failure")

            // Reset for other tests.
            do! fixture.SetMethodError("deleteMessage", false)

            // Confirm still works (bulk_message_id points at the fresh sendMessage).
            let! _ = fixture.SendUpdate(Tg.dmCallback($"addflow:bulk:confirm:{batchId}", user))
            do! waitForBatchCleared fixture batchId 5000

            let! count = fixture.QuerySingle<int64>("SELECT COUNT(*)::bigint FROM coupon WHERE owner_id=@u", {| u = user.Id |})
            Assert.Equal(1L, count)
        }

    [<Fact>]
    let ``Expired coupon at confirm time: item failed with 'истёкший' reason, others insert`` () =
        task {
            do! setupBatchTest ()

            let user = Tg.user(id = 7005L, username = "expired_one", firstName = "Expired")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-expired-{DateTime.UtcNow.Ticks}"
            let files = [
                "expired-1", "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
                "expired-2", "10_50_2026-01-17_2026-01-26_2706688198838.jpg"
            ]
            for fid, fn in files do
                do! fixture.SetTelegramFile(fid, readImageBytes fn)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson (snd files[0]))

            for fid, _ in files do
                let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = fid))
                ()

            let! batchId = waitForBatchByUser fixture user.Id 5000
            do! waitForAllItemsTerminal fixture batchId 10000

            // Force one item's expires_at into the past so TryAddCoupon at confirm
            // returns AddCouponResult.Expired for it. Use the first item's row.
            let! _ = fixture.Execute(
                        "UPDATE pending_add_batch_item SET expires_at = '2000-01-01' WHERE batch_id=@b AND seq=1",
                        {| b = batchId |})
            ()

            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchId "awaiting_user" 5000
            do! waitForBulkConfirmCall fixture user.Id 5000

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback($"addflow:bulk:confirm:{batchId}", user))
            do! waitForBatchCleared fixture batchId 5000

            // Only the second item became a coupon (the first was expired).
            let! count = fixture.QuerySingle<int64>("SELECT COUNT(*)::bigint FROM coupon WHERE owner_id=@u", {| u = user.Id |})
            Assert.Equal(1L, count)

            // Summary edit should mention "истёкший" so the user understands what was skipped.
            // EditBulkOrSend uses editMessageText (with sendMessage fallback) — use the
            // helper that checks both endpoints.
            do! waitForSendMessageOrEditMatching fixture user.Id (fun t -> t.Contains "истёкший") 3000
        }

    // ── Happens-before: OCR vs finalize claim ────────────────────────────

    [<Fact>]
    let ``OCR finishes BEFORE finalize: item is ok, no timeout claim`` () =
        task {
            do! setupBatchTest ()

            let user = Tg.user(id = 7010L, username = "race_fast", firstName = "Fast")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-fast-{DateTime.UtcNow.Ticks}"
            let fid = "race-fast-1"
            let fn = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            do! setupGoodOcr fixture fid fn

            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = fid))

            let! batchId = waitForBatchByUser fixture user.Id 5000
            do! waitForAllItemsTerminal fixture batchId 10000

            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchId "awaiting_user" 5000

            let! statuses =
                fixture.QuerySingle<string>(
                    "SELECT string_agg(i.status, ',' ORDER BY i.seq) FROM pending_add_batch_item i JOIN pending_add_batch b ON b.id=i.batch_id WHERE b.user_id=@u",
                    {| u = user.Id |})
            Assert.Equal("ok", statuses)
        }

    [<Fact>]
    let ``OCR is in-flight when finalize fires: claimed as timeout, late OCR write is no-op`` () =
        task {
            do! setupBatchTest ()

            let user = Tg.user(id = 7011L, username = "race_slow", firstName = "Slow")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-slow-{DateTime.UtcNow.Ticks}"
            let fid = "race-slow-1"
            let fn = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            do! setupGoodOcr fixture fid fn
            // Block the OCR for ~1s of real time at fake-azure so we have time to
            // fire finalize while OCR is still pending.
            do! fixture.SetAzureOcrDelay(1000)

            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = fid))
            let! batchId = waitForBatchByUser fixture user.Id 5000

            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchId "awaiting_user" 5000
            do! waitForBulkConfirmCall fixture user.Id 5000

            let! statusAfterClaim =
                fixture.QuerySingle<string>(
                    "SELECT i.status FROM pending_add_batch_item i JOIN pending_add_batch b ON b.id=i.batch_id WHERE b.user_id=@u",
                    {| u = user.Id |})
            Assert.Equal("needs_input", statusAfterClaim)

            let! noteAfterClaim =
                fixture.QuerySingle<string>(
                    "SELECT i.failure_note FROM pending_add_batch_item i JOIN pending_add_batch b ON b.id=i.batch_id WHERE b.user_id=@u",
                    {| u = user.Id |})
            Assert.Equal("timeout", noteAfterClaim)

            let! calls = fixture.GetFakeCalls("sendMessage")
            let bulkConfirms = bulkConfirmCalls calls user.Id
            Assert.Equal(1, bulkConfirms.Length)
            Assert.True(findCallWithText calls user.Id "не успел обработаться",
                        "Expected per-photo reply with the 'timeout' reason text")

            // Wait deterministically for the late OCR call to actually reach the
            // fake — we know it will because we set a 1s delay on the response.
            do! waitForAzureCallCount fixture 1 2000
            do! Task.Delay 100

            let! statusAfterLateOcr =
                fixture.QuerySingle<string>(
                    "SELECT i.status FROM pending_add_batch_item i JOIN pending_add_batch b ON b.id=i.batch_id WHERE b.user_id=@u",
                    {| u = user.Id |})
            Assert.Equal("needs_input", statusAfterLateOcr)

            let! calls2 = fixture.GetFakeCalls("sendMessage")
            let bulkConfirms2 = bulkConfirmCalls calls2 user.Id
            Assert.Equal(1, bulkConfirms2.Length)
        }

    // ── Webhook non-blocking guarantee ──────────────────────────────────

    [<Fact>]
    let ``Webhook returns fast even when OCR is slow`` () =
        task {
            do! setupBatchTest ()

            let user = Tg.user(id = 7020L, username = "fast_webhook", firstName = "Webhook")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-fast-webhook-{DateTime.UtcNow.Ticks}"
            let fid = "fast-webhook-1"
            let fn = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            do! setupGoodOcr fixture fid fn
            do! fixture.SetAzureOcrDelay(1500)

            let sw = Diagnostics.Stopwatch.StartNew()
            let! resp = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = fid))
            sw.Stop()

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
            Assert.True(sw.ElapsedMilliseconds < 500L,
                        $"Webhook took {sw.ElapsedMilliseconds}ms — should be <500ms because OCR runs in background")
        }

    // ── OCR engine: retry once on transient network error ───────────────

    [<Fact>]
    let ``Network error on first OCR call: second call succeeds, item is ok`` () =
        task {
            do! setupBatchTest ()
            do! fixture.ClearAzureOcrCalls()

            let user = Tg.user(id = 7030L, username = "retry_ok", firstName = "Retry")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-retry-ok-{DateTime.UtcNow.Ticks}"
            let fid = "retry-ok-1"
            let fn = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            do! fixture.SetTelegramFile(fid, readImageBytes fn)

            let goodBody = readAzureCacheJson fn
            do! fixture.SetAzureOcrScript([|
                { status = 200; body = goodBody; delayMs = 0; errorMode = "network" }
                { status = 200; body = goodBody; delayMs = 0; errorMode = "" }
            |])

            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = fid))

            let! batchId = waitForBatchByUser fixture user.Id 5000
            do! waitForAllItemsTerminal fixture batchId 10000

            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchId "awaiting_user" 5000

            let! status =
                fixture.QuerySingle<string>(
                    "SELECT i.status FROM pending_add_batch_item i JOIN pending_add_batch b ON b.id=i.batch_id WHERE b.user_id=@u",
                    {| u = user.Id |})
            Assert.Equal("ok", status)

            let! azureCalls = fixture.GetAzureOcrCalls()
            Assert.True(azureCalls.Length >= 2,
                        $"Expected ≥2 Azure calls (retry); got {azureCalls.Length}")
        }

    [<Fact>]
    let ``Network error on BOTH attempts: item ends up needs_input with OCR failed`` () =
        task {
            do! setupBatchTest ()
            do! fixture.ClearAzureOcrCalls()

            let user = Tg.user(id = 7031L, username = "retry_fail", firstName = "Failed")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-retry-fail-{DateTime.UtcNow.Ticks}"
            let fid = "retry-fail-1"
            let fn = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            do! fixture.SetTelegramFile(fid, readImageBytes fn)
            do! fixture.SetAzureOcrErrorMode("network")

            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = fid))

            let! batchId = waitForBatchByUser fixture user.Id 5000
            do! waitForAllItemsTerminal fixture batchId 10000

            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchId "awaiting_user" 5000

            let! note =
                fixture.QuerySingle<string>(
                    "SELECT i.failure_note FROM pending_add_batch_item i JOIN pending_add_batch b ON b.id=i.batch_id WHERE b.user_id=@u",
                    {| u = user.Id |})
            Assert.Equal("OCR failed", note)

            let! azureCalls = fixture.GetAzureOcrCalls()
            Assert.True(azureCalls.Length >= 2,
                        $"Expected ≥2 Azure calls (one initial + one retry); got {azureCalls.Length}")

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls user.Id "Не получилось распознать",
                        "Expected per-photo reply with OCR-failed text")
        }

    // ── Sequencing: command cancels batch ───────────────────────────────

    [<Fact>]
    let ``Command during open batch deletes the batch`` () =
        task {
            do! setupBatchTest ()

            let user = Tg.user(id = 7040L, username = "cmd_cancel", firstName = "Cmd")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-cmd-{DateTime.UtcNow.Ticks}"
            let fid = "cmd-cancel-1"
            let fn = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            do! setupGoodOcr fixture fid fn

            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = fid))
            let! _ = fixture.SendUpdate(Tg.dmMessage("/list", user))

            let! batches =
                fixture.QuerySingle<int64>(
                    "SELECT COUNT(*)::bigint FROM pending_add_batch WHERE user_id=@u",
                    {| u = user.Id |})
            Assert.Equal(0L, batches)
        }
