namespace CouponHubBot.Tests

open System
open System.Threading.Tasks
open BotTestInfra
open Xunit
open FakeCallHelpers
open BatchTestHelpers

/// BatchRecoveryService startup tests:
///   - bot restarts with an 'open' batch in DB → service re-OCRs pending items
///     and re-arms the debounce timer
type BatchRecoveryTests(fixture: OcrCouponHubTestContainers) =

    let setupBatchTest () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            do! fixture.TruncateBatches()
            do! resetOcrFakes fixture
        }

    let goodFile = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"

    [<Fact>]
    let ``Open batch persists across restart: recovery service re-OCRs and finalizes`` () =
        task {
            do! setupBatchTest ()
            let user = Tg.user(id = 7700L, username = "recovery", firstName = "Rec")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            // Step 1: insert an 'open' batch directly via SQL, simulating a batch
            // the bot was processing when it crashed. We DON'T go through the
            // webhook because that would also schedule an in-process timer; we
            // want to test the recovery path specifically.
            let! _ =
                fixture.Execute(
                    """
                    INSERT INTO "user"(id, username, first_name, created_at, updated_at)
                    VALUES (@u, 'recuser', 'Rec', NOW(), NOW())
                    ON CONFLICT (id) DO NOTHING;
                    """, {| u = user.Id |})
            let mgid = $"mg-recov-{DateTime.UtcNow.Ticks}"
            let! batchId =
                fixture.QuerySingle<int64>(
                    """
                    INSERT INTO pending_add_batch(user_id, media_group_id, bulk_chat_id, status)
                    VALUES (@u, @mg, @u, 'open')
                    RETURNING id;
                    """, {| u = user.Id; mg = mgid |})

            // Add one pending item — recovery will re-OCR it.
            do! fixture.SetTelegramFile("rec-1", readImageBytes goodFile)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson goodFile)
            let! _ =
                fixture.Execute(
                    """
                    INSERT INTO pending_add_batch_item(batch_id, seq, photo_file_id, photo_message_id, status)
                    VALUES (@b, 1, 'rec-1', 9701, 'pending');
                    """, {| b = batchId |})

            // Step 2: restart the bot. BatchRecoveryService runs in IHostedService.StartAsync
            // and should find the open batch, kick off OcrItem for the pending row,
            // and re-arm the debounce timer.
            do! fixture.RestartBotApp()

            // Step 3: wait for OCR to complete on the item (recovery should have
            // kicked it off).
            do! waitForAllItemsTerminal fixture batchId 15000

            // Step 4: advance the (FRESH bot's) clock past debounce → finalize fires.
            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchId "awaiting_user" 5000
            do! waitForBulkConfirmCall fixture user.Id 5000

            // Step 5: confirm and verify the coupon was added.
            let! _ = fixture.SendUpdate(Tg.dmCallback($"addflow:bulk:confirm:{batchId}", user))
            do! waitForBatchCleared fixture batchId 5000

            let! count =
                fixture.QuerySingle<int64>(
                    "SELECT COUNT(*)::bigint FROM coupon WHERE owner_id=@u",
                    {| u = user.Id |})
            Assert.Equal(1L, count)
        }
