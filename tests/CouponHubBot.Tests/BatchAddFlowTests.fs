namespace CouponHubBot.Tests

open BotTestInfra
open System
open System.IO
open System.Net
open System.Text.Json
open System.Threading.Tasks
open DotNet.Testcontainers.Builders
open Xunit
open FakeCallHelpers

/// Tests for album-upload batch flow (V16 / pending_add_batch).
///
/// In TestMode the bot uses FakeTimeProvider; the debounce timer in
/// BatchDebounce is scheduled through it, so the timer never fires on real
/// wall clock during a test. Each test composes a deterministic sequence:
///
///   send photo(s) → poll until items reach the expected state
///                 → fixture.AdvanceBotClock to fire finalize
///                 → poll until batch reaches awaiting_user (or is cleared)
///                 → assert
///
/// The polling helpers (waitForBatchByUser, waitForAllItemsTerminal,
/// waitForBatchStatus, waitForBatchCleared, waitForAzureCallCount) fail
/// loudly on timeout, so flakes surface as clear "timed out waiting for X"
/// errors instead of stale state passing assertions.
type BatchAddFlowTests(fixture: OcrCouponHubTestContainers) =

    let solutionDirPath = CommonDirectoryPath.GetSolutionDirectory().DirectoryPath

    /// Past the seeded BATCH_DEBOUNCE_MS (30000) so the debounce timer fires
    /// when Advance is called. Per-test clock drift = ~31s; bounded and
    /// harmless to other tests.
    let advancePastDebounce () = fixture.AdvanceBotClock(31_000)

    let readImageBytes (fileName: string) =
        File.ReadAllBytes(Path.Combine(solutionDirPath, "tests", "CouponHubBot.Ocr.Tests", "Images", fileName))

    let readAzureCacheJson (fileName: string) =
        File.ReadAllText(Path.Combine(solutionDirPath, "tests", "CouponHubBot.Ocr.Tests", "AzureCache", fileName + ".azure.json"))

    let getCouponCount () =
        fixture.QuerySingle<int64>("SELECT COUNT(*)::bigint FROM coupon", null)

    let getBatchCount () =
        fixture.QuerySingle<int64>("SELECT COUNT(*)::bigint FROM pending_add_batch", null)

    /// Counts placeholder sendMessage calls in the user's chat.
    let placeholderCalls (calls: FakeCall array) (chatId: int64) =
        calls
        |> Array.filter (fun call ->
            match parseCallBody call.Body with
            | Some p when p.ChatId = Some chatId ->
                match p.Text with
                | Some t -> t.Contains("обрабатываю купоны")
                | _ -> false
            | _ -> false)

    let bulkConfirmCalls (calls: FakeCall array) (chatId: int64) =
        calls
        |> Array.filter (fun call ->
            match parseCallBody call.Body with
            | Some p when p.ChatId = Some chatId ->
                match p.Text with
                | Some t -> t.Contains("Подтвердить") || t.Contains("Не смог распознать ни одного")
                | _ -> false
            | _ -> false)

    let resetOcrFakes () =
        task {
            do! fixture.SetAzureOcrDelay(0)
            do! fixture.SetAzureOcrErrorMode("")
            do! fixture.SetAzureOcrScript([||])
        }

    let setupGoodOcr (fileId: string) (fileName: string) =
        task {
            do! fixture.SetTelegramFile(fileId, readImageBytes fileName)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson fileName)
        }

    // ── Polling helpers ─────────────────────────────────────────────────
    //
    // Each one polls the DB / fake-azure with a short interval and a hard
    // timeout. On timeout they throw with a clear message so the test fails
    // at the poll site, not three asserts later on stale state.

    let pollIntervalMs = 25

    let waitForBatchByUser (userId: int64) (timeoutMs: int) =
        task {
            let sw = Diagnostics.Stopwatch.StartNew()
            let mutable batchId = 0L
            while sw.ElapsedMilliseconds < int64 timeoutMs && batchId = 0L do
                let! id =
                    fixture.QuerySingle<int64>(
                        "SELECT COALESCE((SELECT id FROM pending_add_batch WHERE user_id=@u ORDER BY id DESC LIMIT 1), 0)",
                        {| u = userId |})
                batchId <- id
                if batchId = 0L then do! Task.Delay pollIntervalMs
            if batchId = 0L then
                return failwith $"Timeout: no batch row for user {userId} after {timeoutMs}ms"
            else
                return batchId
        }

    let waitForAllItemsTerminal (batchId: int64) (timeoutMs: int) =
        task {
            let sw = Diagnostics.Stopwatch.StartNew()
            let mutable pendingCount = -1L
            while sw.ElapsedMilliseconds < int64 timeoutMs && pendingCount <> 0L do
                let! c =
                    fixture.QuerySingle<int64>(
                        "SELECT COUNT(*)::bigint FROM pending_add_batch_item WHERE batch_id=@b AND status='pending'",
                        {| b = batchId |})
                pendingCount <- c
                if pendingCount <> 0L then do! Task.Delay pollIntervalMs
            if pendingCount <> 0L then
                failwith $"Timeout: {pendingCount} item(s) still pending in batch {batchId} after {timeoutMs}ms"
        }

    let waitForBatchStatus (batchId: int64) (expected: string) (timeoutMs: int) =
        task {
            let sw = Diagnostics.Stopwatch.StartNew()
            let mutable status = ""
            while sw.ElapsedMilliseconds < int64 timeoutMs && status <> expected do
                let! s =
                    fixture.QuerySingle<string>(
                        "SELECT COALESCE((SELECT status FROM pending_add_batch WHERE id=@b LIMIT 1), '__GONE__')",
                        {| b = batchId |})
                status <- s
                if status <> expected then do! Task.Delay pollIntervalMs
            if status <> expected then
                failwith $"Timeout: batch {batchId} status is '{status}', expected '{expected}' after {timeoutMs}ms"
        }

    let waitForBatchCleared (batchId: int64) (timeoutMs: int) =
        task {
            let sw = Diagnostics.Stopwatch.StartNew()
            let mutable exists = true
            while sw.ElapsedMilliseconds < int64 timeoutMs && exists do
                let! c =
                    fixture.QuerySingle<int64>(
                        "SELECT COUNT(*)::bigint FROM pending_add_batch WHERE id=@b",
                        {| b = batchId |})
                exists <- c > 0L
                if exists then do! Task.Delay pollIntervalMs
            if exists then
                failwith $"Timeout: batch {batchId} not cleared after {timeoutMs}ms"
        }

    let waitForAzureCallCount (expected: int) (timeoutMs: int) =
        task {
            let sw = Diagnostics.Stopwatch.StartNew()
            let mutable count = 0
            while sw.ElapsedMilliseconds < int64 timeoutMs && count < expected do
                let! calls = fixture.GetAzureOcrCalls()
                count <- calls.Length
                if count < expected then do! Task.Delay pollIntervalMs
            if count < expected then
                failwith $"Timeout: Azure OCR call count is {count}, expected ≥{expected} after {timeoutMs}ms"
        }

    /// Wait for the bulk-confirm sendMessage to actually land in the fake-tg call log.
    /// `waitForBatchStatus "awaiting_user"` is satisfied the moment FinalizeBatch flips
    /// the row's status — but the SendMessage that produces the bulk-confirm call
    /// happens a few async steps LATER inside the same FinalizeBatch task. Polling
    /// the DB status returns too early, so we poll the fake-tg log directly here.
    let waitForBulkConfirmCall (userId: int64) (timeoutMs: int) =
        task {
            let sw = Diagnostics.Stopwatch.StartNew()
            let mutable found = false
            while sw.ElapsedMilliseconds < int64 timeoutMs && not found do
                let! calls = fixture.GetFakeCalls("sendMessage")
                let bulks = bulkConfirmCalls calls userId
                found <- bulks.Length > 0
                if not found then do! Task.Delay pollIntervalMs
            if not found then
                failwith $"Timeout: no bulk-confirm sendMessage for user {userId} after {timeoutMs}ms"
        }

    // ── Happy path ──────────────────────────────────────────────────────

    [<Fact>]
    let ``Album of 3 OK photos: one placeholder, one bulk-confirm, confirm adds 3 coupons`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            do! fixture.TruncateBatches()
            do! resetOcrFakes ()

            let user = Tg.user(id = 7001L, username = "album_happy", firstName = "Album")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-happy-{DateTime.UtcNow.Ticks}"

            // Three album photos sharing media_group_id. Each photo's barcode is
            // decoded by ZXing from real image bytes per fileId, so all three are
            // unique → 3 ok items.
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

            let! batchId = waitForBatchByUser user.Id 5000
            do! waitForAllItemsTerminal batchId 10000

            do! advancePastDebounce ()
            do! waitForBatchStatus batchId "awaiting_user" 5000
            // Status flips BEFORE the bulk-confirm sendMessage in FinalizeBatch — wait
            // for the actual call to land before asserting on it.
            do! waitForBulkConfirmCall user.Id 5000

            let! calls = fixture.GetFakeCalls("sendMessage")
            let placeholders = placeholderCalls calls user.Id
            let bulkConfirms = bulkConfirmCalls calls user.Id

            Assert.Equal(1, placeholders.Length)
            Assert.Equal(1, bulkConfirms.Length)

            let! deletes = fixture.GetFakeCalls("deleteMessage")
            Assert.True(deletes.Length >= 1, $"Expected ≥1 deleteMessage; got {deletes.Length}")

            Assert.True(
                findCallWithText calls user.Id "Подтвердить 3 купонов",
                "Expected bulk-confirm text 'Подтвердить 3 купонов' in sendMessage calls")

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback($"addflow:bulk:confirm:{batchId}", user))
            do! waitForBatchCleared batchId 5000

            let! count = getCouponCount ()
            Assert.Equal(3L, count)
            let! batches = getBatchCount ()
            Assert.Equal(0L, batches)
        }

    [<Fact>]
    let ``Album of 1 photo: batch path still works, confirm adds 1`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            do! fixture.TruncateBatches()
            do! resetOcrFakes ()

            let user = Tg.user(id = 7002L, username = "album_one", firstName = "Solo")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-one-{DateTime.UtcNow.Ticks}"
            let fid = "album-one-1"
            let fn = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            do! setupGoodOcr fid fn

            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = fid))

            let! batchId = waitForBatchByUser user.Id 5000
            do! waitForAllItemsTerminal batchId 10000

            do! advancePastDebounce ()
            do! waitForBatchStatus batchId "awaiting_user" 5000
            do! waitForBulkConfirmCall user.Id 5000

            let! calls = fixture.GetFakeCalls("sendMessage")
            let bulkConfirms = bulkConfirmCalls calls user.Id
            Assert.Equal(1, bulkConfirms.Length)
            Assert.True(
                findCallWithText calls user.Id "Подтвердить 1",
                "Expected bulk-confirm text 'Подтвердить 1' in sendMessage calls")

            let! _ = fixture.SendUpdate(Tg.dmCallback($"addflow:bulk:confirm:{batchId}", user))
            do! waitForBatchCleared batchId 5000

            let! count = fixture.QuerySingle<int64>("SELECT COUNT(*)::bigint FROM coupon WHERE owner_id=@u", {| u = user.Id |})
            Assert.Equal(1L, count)
        }

    // ── Happens-before: OCR vs finalize claim ────────────────────────────

    [<Fact>]
    let ``OCR finishes BEFORE finalize: item is ok, no timeout claim`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            do! fixture.TruncateBatches()
            do! resetOcrFakes ()

            let user = Tg.user(id = 7010L, username = "race_fast", firstName = "Fast")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-fast-{DateTime.UtcNow.Ticks}"
            let fid = "race-fast-1"
            let fn = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            do! setupGoodOcr fid fn

            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = fid))

            let! batchId = waitForBatchByUser user.Id 5000
            // Deterministic: wait until OCR has actually written its result.
            do! waitForAllItemsTerminal batchId 10000

            do! advancePastDebounce ()
            do! waitForBatchStatus batchId "awaiting_user" 5000

            let! statuses =
                fixture.QuerySingle<string>(
                    "SELECT string_agg(i.status, ',' ORDER BY i.seq) FROM pending_add_batch_item i JOIN pending_add_batch b ON b.id=i.batch_id WHERE b.user_id=@u",
                    {| u = user.Id |})
            Assert.Equal("ok", statuses)
        }

    [<Fact>]
    let ``OCR is in-flight when finalize fires: claimed as timeout, late OCR write is no-op`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            do! fixture.TruncateBatches()
            do! resetOcrFakes ()

            let user = Tg.user(id = 7011L, username = "race_slow", firstName = "Slow")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-slow-{DateTime.UtcNow.Ticks}"
            let fid = "race-slow-1"
            let fn = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            do! setupGoodOcr fid fn
            // Block the OCR for ~1s of real time at fake-azure so we have time to
            // fire finalize while OCR is still pending. The wait at the end is
            // bounded by this delay — not racy.
            do! fixture.SetAzureOcrDelay(1000)

            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = fid))
            let! batchId = waitForBatchByUser user.Id 5000

            // Immediately advance the bot clock. OCR is blocked at the fake (~1s).
            // FinalizeBatch is fast (a few DB writes + sendMessage) so it wins.
            do! advancePastDebounce ()
            do! waitForBatchStatus batchId "awaiting_user" 5000
            do! waitForBulkConfirmCall user.Id 5000

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
            // The 2s budget is double the delay; if the call somehow never lands
            // the test fails loudly here, not by hiding a regression below.
            do! waitForAzureCallCount 1 2000
            // Plus a short tail so the bot has a chance to write (which it shouldn't,
            // because UPDATE … WHERE status='pending' will match zero rows).
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
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            do! fixture.TruncateBatches()
            do! resetOcrFakes ()

            let user = Tg.user(id = 7020L, username = "fast_webhook", firstName = "Webhook")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-fast-webhook-{DateTime.UtcNow.Ticks}"
            let fid = "fast-webhook-1"
            let fn = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            do! setupGoodOcr fid fn
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
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            do! fixture.TruncateBatches()
            do! resetOcrFakes ()
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

            let! batchId = waitForBatchByUser user.Id 5000
            // No more race against debounce — debounce won't fire until we Advance.
            // Just wait for the retry chain (error → 100ms sleep → success) to land.
            do! waitForAllItemsTerminal batchId 10000

            do! advancePastDebounce ()
            do! waitForBatchStatus batchId "awaiting_user" 5000

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
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            do! fixture.TruncateBatches()
            do! resetOcrFakes ()
            do! fixture.ClearAzureOcrCalls()

            let user = Tg.user(id = 7031L, username = "retry_fail", firstName = "Failed")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-retry-fail-{DateTime.UtcNow.Ticks}"
            let fid = "retry-fail-1"
            let fn = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            do! fixture.SetTelegramFile(fid, readImageBytes fn)
            do! fixture.SetAzureOcrErrorMode("network")

            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = fid))

            let! batchId = waitForBatchByUser user.Id 5000
            // Both attempts fail; OcrItem's outer catch writes "OCR failed" → item
            // becomes needs_input. Poll for that state — no debounce race because
            // the timer is FakeTimeProvider-driven.
            do! waitForAllItemsTerminal batchId 10000

            do! advancePastDebounce ()
            do! waitForBatchStatus batchId "awaiting_user" 5000

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
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            do! fixture.TruncateBatches()
            do! resetOcrFakes ()

            let user = Tg.user(id = 7040L, username = "cmd_cancel", firstName = "Cmd")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-cmd-{DateTime.UtcNow.Ticks}"
            let fid = "cmd-cancel-1"
            let fn = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            do! setupGoodOcr fid fn

            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = fid))
            // No need to advance the clock — the command itself clears the batch
            // synchronously (BotService runs db.AbandonOpenBatchesExcept on any
            // command). FakeTimeProvider means the debounce won't fire to race us.
            let! _ = fixture.SendUpdate(Tg.dmMessage("/list", user))

            let! batches =
                fixture.QuerySingle<int64>(
                    "SELECT COUNT(*)::bigint FROM pending_add_batch WHERE user_id=@u",
                    {| u = user.Id |})
            Assert.Equal(0L, batches)
        }
