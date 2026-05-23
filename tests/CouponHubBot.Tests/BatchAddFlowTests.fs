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
/// The fixture seeds BATCH_DEBOUNCE_MS=200 so each test only needs ~400ms
/// after the last photo to observe the finalize handler's output.
type BatchAddFlowTests(fixture: OcrCouponHubTestContainers) =

    let solutionDirPath = CommonDirectoryPath.GetSolutionDirectory().DirectoryPath

    let readImageBytes (fileName: string) =
        File.ReadAllBytes(Path.Combine(solutionDirPath, "tests", "CouponHubBot.Ocr.Tests", "Images", fileName))

    let readAzureCacheJson (fileName: string) =
        File.ReadAllText(Path.Combine(solutionDirPath, "tests", "CouponHubBot.Ocr.Tests", "AzureCache", fileName + ".azure.json"))

    /// Azure JSON with any 13-digit barcode text line removed (forces
    /// CouponOcrEngine's text-fallback to NOT find a barcode either).
    let stripBarcodeFromAzureJson (azureJson: string) =
        let doc = JsonDocument.Parse(azureJson)
        use ms = new MemoryStream()
        let opts = JsonWriterOptions(Indented = false)
        use writer = new Utf8JsonWriter(ms, opts)
        let rec writeElement (el: JsonElement) =
            match el.ValueKind with
            | JsonValueKind.Object ->
                writer.WriteStartObject()
                for prop in el.EnumerateObject() do
                    writer.WritePropertyName(prop.Name)
                    writeElement prop.Value
                writer.WriteEndObject()
            | JsonValueKind.Array ->
                writer.WriteStartArray()
                let mutable items = el.EnumerateArray() |> Seq.toArray
                if items.Length > 0
                   && items |> Array.exists (fun it ->
                       it.ValueKind = JsonValueKind.Object
                       && (match it.TryGetProperty("text") with
                           | true, t -> System.Text.RegularExpressions.Regex.IsMatch(t.GetString(), @"^\d{13}$")
                           | _ -> false)) then
                    items <- items |> Array.filter (fun it ->
                        not (it.ValueKind = JsonValueKind.Object
                             && (match it.TryGetProperty("text") with
                                 | true, t -> System.Text.RegularExpressions.Regex.IsMatch(t.GetString(), @"^\d{13}$")
                                 | _ -> false)))
                for item in items do
                    writeElement item
                writer.WriteEndArray()
            | _ -> el.WriteTo(writer)
        writeElement doc.RootElement
        writer.Flush()
        System.Text.Encoding.UTF8.GetString(ms.ToArray())

    let getCouponCount () =
        fixture.QuerySingle<int64>("SELECT COUNT(*)::bigint FROM coupon", null)

    let getBatchCount () =
        fixture.QuerySingle<int64>("SELECT COUNT(*)::bigint FROM pending_add_batch", null)

    /// Counts placeholder sendMessage calls in the user's chat (best-effort: the
    /// placeholder text varies). Returns the calls whose text contains the
    /// placeholder marker substring.
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

    // ── Happy path ──────────────────────────────────────────────────────

    [<Fact>]
    let ``Album of 3 OK photos: one placeholder, one bulk-confirm, confirm adds 3 coupons`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            do! resetOcrFakes ()

            let user = Tg.user(id = 7001L, username = "album_happy", firstName = "Album")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-happy-{DateTime.UtcNow.Ticks}"
            let fname = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson fname)

            // Three album photos sharing media_group_id. Same image bytes (and barcode) —
            // but distinct photo_file_ids, so UNIQUE (batch_id, photo_file_id) doesn't dedupe.
            // Coupon dedup happens at TryAddCoupon time on confirm; here all three barcodes
            // are identical, so we expect 1 inserted + 2 skipped as duplicates.
            // For "3 inserted", point each file_id at a different image.
            let files = [
                "album-happy-1", "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
                "album-happy-2", "10_50_2026-01-17_2026-01-26_2706688198838.jpg"
                "album-happy-3", "10_50_2026-01-17_2026-01-26_2706688198821.jpg"
            ]
            for fid, fn in files do
                do! fixture.SetTelegramFile(fid, readImageBytes fn)

            // Telegram delivers photos with the SAME azure response in this test —
            // each photo's barcode is decoded by ZXing locally from its real bytes,
            // so unique barcodes per image.
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson (snd files[0]))

            for fid, _ in files do
                let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = fid))
                ()

            // Wait past debounce + render.
            do! Task.Delay 600

            let! calls = fixture.GetFakeCalls("sendMessage")
            let placeholders = placeholderCalls calls user.Id
            let bulkConfirms = bulkConfirmCalls calls user.Id

            Assert.Equal(1, placeholders.Length)
            Assert.Equal(1, bulkConfirms.Length)

            let! deletes = fixture.GetFakeCalls("deleteMessage")
            // The placeholder gets deleted so the fresh bulk-confirm pushes a notification.
            Assert.True(deletes.Length >= 1, $"Expected ≥1 deleteMessage; got {deletes.Length}")

            // Bulk-confirm message should advertise 3 items (each photo's barcode is unique
            // because ZXing decodes from real image bytes per fileId).
            Assert.Contains("Подтвердить 3 купонов", bulkConfirms[0].Body)

            // Capture the batch id from DB so we can invoke confirm.
            let! batchId = fixture.QuerySingle<int64>("SELECT id FROM pending_add_batch WHERE user_id=@u", {| u = user.Id |})

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback($"addflow:bulk:confirm:{batchId}", user))

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
            do! resetOcrFakes ()

            let user = Tg.user(id = 7002L, username = "album_one", firstName = "Solo")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-one-{DateTime.UtcNow.Ticks}"
            let fid = "album-one-1"
            let fn = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            do! setupGoodOcr fid fn

            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = fid))
            do! Task.Delay 400

            let! calls = fixture.GetFakeCalls("sendMessage")
            let bulkConfirms = bulkConfirmCalls calls user.Id
            Assert.Equal(1, bulkConfirms.Length)
            Assert.Contains("Подтвердить 1", bulkConfirms[0].Body)

            let! batchId = fixture.QuerySingle<int64>("SELECT id FROM pending_add_batch WHERE user_id=@u", {| u = user.Id |})
            let! _ = fixture.SendUpdate(Tg.dmCallback($"addflow:bulk:confirm:{batchId}", user))

            let! count = fixture.QuerySingle<int64>("SELECT COUNT(*)::bigint FROM coupon WHERE owner_id=@u", {| u = user.Id |})
            Assert.Equal(1L, count)
        }

    // ── Happens-before: OCR vs finalize claim ────────────────────────────

    [<Fact>]
    let ``OCR finishes BEFORE finalize: item is ok, no timeout claim`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            do! resetOcrFakes ()

            let user = Tg.user(id = 7010L, username = "race_fast", firstName = "Fast")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-fast-{DateTime.UtcNow.Ticks}"
            let fid = "race-fast-1"
            let fn = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            do! setupGoodOcr fid fn
            do! fixture.SetAzureOcrDelay(50) // ≪ 200ms debounce

            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = fid))
            do! Task.Delay 600

            let! statuses = fixture.QuerySingle<string>(
                                "SELECT string_agg(status, ',' ORDER BY seq) FROM pending_add_batch_item i JOIN pending_add_batch b ON b.id=i.batch_id WHERE b.user_id=@u",
                                {| u = user.Id |})
            Assert.Equal("ok", statuses)
        }

    [<Fact>]
    let ``OCR is in-flight when finalize fires: claimed as timeout, late OCR write is no-op`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            do! resetOcrFakes ()

            let user = Tg.user(id = 7011L, username = "race_slow", firstName = "Slow")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-slow-{DateTime.UtcNow.Ticks}"
            let fid = "race-slow-1"
            let fn = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            do! setupGoodOcr fid fn
            // OCR delay > debounce: by the time finalize fires, OCR has not yet committed.
            do! fixture.SetAzureOcrDelay(800)

            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = fid))
            // Wait past debounce (200ms) but before OCR (800ms).
            do! Task.Delay 400

            let! statusAfterClaim = fixture.QuerySingle<string>(
                                        "SELECT i.status FROM pending_add_batch_item i JOIN pending_add_batch b ON b.id=i.batch_id WHERE b.user_id=@u",
                                        {| u = user.Id |})
            Assert.Equal("needs_input", statusAfterClaim)

            let! noteAfterClaim = fixture.QuerySingle<string>(
                                      "SELECT i.failure_note FROM pending_add_batch_item i JOIN pending_add_batch b ON b.id=i.batch_id WHERE b.user_id=@u",
                                      {| u = user.Id |})
            Assert.Equal("timeout", noteAfterClaim)

            let! calls = fixture.GetFakeCalls("sendMessage")
            let bulkConfirms = bulkConfirmCalls calls user.Id
            Assert.Equal(1, bulkConfirms.Length)

            // Per-photo reply with the "не успел" wording.
            Assert.True(findCallWithText calls user.Id "не успел обработаться",
                        "Expected per-photo reply with the 'timeout' reason text")

            // Wait for the late OCR to land. It MUST NOT clobber the timeout status.
            do! Task.Delay 700

            let! statusAfterLateOcr = fixture.QuerySingle<string>(
                                          "SELECT i.status FROM pending_add_batch_item i JOIN pending_add_batch b ON b.id=i.batch_id WHERE b.user_id=@u",
                                          {| u = user.Id |})
            Assert.Equal("needs_input", statusAfterLateOcr)

            // Critical: no SECOND bulk-confirm fired after the late OCR.
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
            // OCR runs in background; the webhook handler does only DB + placeholder send.
            // 500ms is loose enough to tolerate container jitter.
            Assert.True(sw.ElapsedMilliseconds < 500L,
                        $"Webhook took {sw.ElapsedMilliseconds}ms — should be <500ms because OCR runs in background")
        }

    // ── OCR engine: retry once on transient network error ───────────────

    [<Fact>]
    let ``Network error on first OCR call: second call succeeds, item is ok`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
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
            do! Task.Delay 600

            let! status = fixture.QuerySingle<string>(
                              "SELECT i.status FROM pending_add_batch_item i JOIN pending_add_batch b ON b.id=i.batch_id WHERE b.user_id=@u",
                              {| u = user.Id |})
            Assert.Equal("ok", status)

            let! azureCalls = fixture.GetAzureOcrCalls()
            // Both the failing first attempt and the successful retry hit the fake.
            Assert.True(azureCalls.Length >= 2,
                        $"Expected ≥2 Azure calls (retry); got {azureCalls.Length}")
        }

    [<Fact>]
    let ``Network error on BOTH attempts: item ends up needs_input with OCR failed`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
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
            do! Task.Delay 600

            let! note = fixture.QuerySingle<string>(
                            "SELECT i.failure_note FROM pending_add_batch_item i JOIN pending_add_batch b ON b.id=i.batch_id WHERE b.user_id=@u",
                            {| u = user.Id |})
            Assert.Equal("OCR failed", note)

            let! azureCalls = fixture.GetAzureOcrCalls()
            Assert.True(azureCalls.Length >= 2,
                        $"Expected ≥2 Azure calls (one initial + one retry); got {azureCalls.Length}")

            // User-facing per-photo reply uses the OCR-failed wording.
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
            do! resetOcrFakes ()

            let user = Tg.user(id = 7040L, username = "cmd_cancel", firstName = "Cmd")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-cmd-{DateTime.UtcNow.Ticks}"
            let fid = "cmd-cancel-1"
            let fn = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            do! setupGoodOcr fid fn

            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = fid))
            // Send a command BEFORE debounce fires.
            let! _ = fixture.SendUpdate(Tg.dmMessage("/list", user))

            let! batches = fixture.QuerySingle<int64>(
                               "SELECT COUNT(*)::bigint FROM pending_add_batch WHERE user_id=@u",
                               {| u = user.Id |})
            Assert.Equal(0L, batches)
        }
