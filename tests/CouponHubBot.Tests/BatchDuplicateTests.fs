namespace CouponHubBot.Tests

open System
open System.Threading.Tasks
open BotTestInfra
open Xunit
open FakeCallHelpers
open BatchTestHelpers

/// Duplicate-handling tests:
///   - Barcode duplicate vs existing coupon (TryAddCoupon.DuplicateBarcode at confirm)
///   - Same photo_file_id redelivered within a batch (UNIQUE (batch_id, photo_file_id))
type BatchDuplicateTests(fixture: OcrCouponHubTestContainers) =

    let setupBatchTest () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            do! fixture.TruncateBatches()
            do! resetOcrFakes fixture
        }

    let goodFile = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"

    [<Fact>]
    let ``Barcode duplicate vs existing coupon: 1 item failed at confirm, others inserted`` () =
        task {
            do! setupBatchTest ()
            let user = Tg.user(id = 7500L, username = "dup_barcode", firstName = "Dup")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            // Pre-insert a coupon with the same barcode that will be on one of the
            // album photos. The barcode is encoded in the filename: 2706688198845.
            let preExistingBarcode = "2706688198845"
            // Use a future date so the coupon is active (TryAddCoupon's barcode
            // dedup check fires for active coupons).
            let! _ =
                fixture.Execute(
                    """
                    INSERT INTO "user"(id, username, first_name, created_at, updated_at)
                    VALUES (@u, 'preuser', 'Pre', NOW(), NOW())
                    ON CONFLICT (id) DO NOTHING;
                    """, {| u = user.Id |})
            let! _ =
                fixture.Execute(
                    """
                    INSERT INTO coupon(owner_id, photo_file_id, value, min_check, expires_at, barcode_text, status)
                    VALUES (@u, 'preexisting-photo', 10.00, 50.00, (now() + interval '30 days')::date, @bc, 'available');
                    """, {| u = user.Id; bc = preExistingBarcode |})

            // Send an album of 3. The first photo's barcode will match the
            // pre-existing one. The other two are unique.
            let mgid = $"mg-dupbc-{DateTime.UtcNow.Ticks}"
            do! fixture.SetTelegramFile("dupbc-1", readImageBytes goodFile)  // contains barcode 2706688198845
            do! fixture.SetTelegramFile("dupbc-2", readImageBytes "10_50_2026-01-17_2026-01-26_2706688198838.jpg")
            do! fixture.SetTelegramFile("dupbc-3", readImageBytes "10_50_2026-01-17_2026-01-26_2706688198821.jpg")
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson goodFile)

            for fid in ["dupbc-1"; "dupbc-2"; "dupbc-3"] do
                let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = fid))
                ()

            let! batchId = waitForBatchByUser fixture user.Id 5000
            do! waitForAllItemsTerminal fixture batchId 10000
            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchId "awaiting_user" 5000
            do! waitForBulkConfirmCall fixture user.Id 5000

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback($"addflow:bulk:confirm:{batchId}", user))
            do! waitForBatchCleared fixture batchId 5000

            // The 2 unique-barcode photos should have been inserted; the 1
            // duplicate-barcode photo failed.
            let! addedCount =
                fixture.QuerySingle<int64>(
                    "SELECT COUNT(*)::bigint FROM coupon WHERE owner_id=@u AND barcode_text <> @bc",
                    {| u = user.Id; bc = preExistingBarcode |})
            Assert.Equal(2L, addedCount)

            // Summary mentions the duplicate.
            do! waitForSendMessageOrEditMatching fixture user.Id
                    (fun t -> t.Contains "дубликат") 3000
        }

    [<Fact>]
    let ``Same photo_file_id redelivered within batch: UNIQUE (batch_id, photo_file_id) means only 1 item`` () =
        task {
            do! setupBatchTest ()
            let user = Tg.user(id = 7501L, username = "dup_photo_id", firstName = "DupFid")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-dupfid-{DateTime.UtcNow.Ticks}"
            do! fixture.SetTelegramFile("dupfid-1", readImageBytes goodFile)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson goodFile)

            // Send the SAME photo_file_id twice (simulating Telegram redelivery).
            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = "dupfid-1", messageId = 9601))
            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = "dupfid-1", messageId = 9602))

            let! batchId = waitForBatchByUser fixture user.Id 5000
            // Item count should stabilize at 1 (the second AddBatchItem is a no-op).
            do! Task.Delay 300
            let! itemCount =
                fixture.QuerySingle<int64>(
                    "SELECT COUNT(*)::bigint FROM pending_add_batch_item WHERE batch_id=@b",
                    {| b = batchId |})
            Assert.Equal(1L, itemCount)

            do! waitForAllItemsTerminal fixture batchId 10000
            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchId "awaiting_user" 5000
            do! waitForBulkConfirmCall fixture user.Id 5000

            let! calls = fixture.GetFakeCalls("sendMessage")
            // Bulk-confirm advertises 1 ok item (not 2).
            Assert.True(findCallWithText calls user.Id "Подтвердить 1",
                        "Expected 'Подтвердить 1' (single item, despite redelivery)")
        }

    [<Fact>]
    let ``Two photos in same album with identical barcode: 1 inserts at confirm, 1 fails`` () =
        task {
            do! setupBatchTest ()
            let user = Tg.user(id = 7502L, username = "dup_within_album", firstName = "DupSameAlb")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            // Both photos use the same image (same barcode after ZXing), but with
            // distinct photo_file_ids — so AddBatchItem creates two items.
            let mgid = $"mg-dupalb-{DateTime.UtcNow.Ticks}"
            do! fixture.SetTelegramFile("dupalb-1", readImageBytes goodFile)
            do! fixture.SetTelegramFile("dupalb-2", readImageBytes goodFile)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson goodFile)

            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = "dupalb-1"))
            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = "dupalb-2"))

            let! batchId = waitForBatchByUser fixture user.Id 5000
            do! waitForItemCount fixture batchId 2 5000
            do! waitForAllItemsTerminal fixture batchId 10000
            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchId "awaiting_user" 5000
            do! waitForBulkConfirmCall fixture user.Id 5000

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback($"addflow:bulk:confirm:{batchId}", user))
            do! waitForBatchCleared fixture batchId 5000

            // Only ONE coupon ends up in the table — the second item's TryAddCoupon
            // returned DuplicateBarcode (or DuplicatePhoto on photo_file_id collision).
            let! count = fixture.QuerySingle<int64>("SELECT COUNT(*)::bigint FROM coupon WHERE owner_id=@u", {| u = user.Id |})
            Assert.Equal(1L, count)
        }
