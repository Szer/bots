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

    /// Crash mid-finalize: the bot ran TryFlipBatchToAwaiting (status='awaiting_user')
    /// and may even have ClaimPendingItemsAsTimeout / GetBatchItems, but died
    /// BEFORE sending the bulk-confirm SendMessage and SetBatchBulkMessageId.
    /// The batch row sits in 'awaiting_user' with bulk_message_id=NULL — the
    /// user has no actionable UI in their chat and no message to click.
    ///
    /// Today's behavior: BatchRecoveryService deliberately skips awaiting_user
    /// rows (only re-OCRs 'open' batches), and the TTL reaper in
    /// CreateBatchAtomically only deletes status='open' rows. So this batch
    /// LEAKS forever unless the user uploads another album (which would
    /// abandon it).
    ///
    /// EXPECTED behavior: either (a) recovery re-runs finalize for awaiting_user
    /// batches with bulk_message_id=NULL (re-sending the bulk-confirm), or
    /// (b) the TTL housekeeping reaps stale awaiting_user rows. This test
    /// asserts that ONE of those happens; today it fails, documenting the gap.
    [<Fact>]
    let ``Crash mid-finalize leaves awaiting_user batch with NULL bulk_message_id: recovery or TTL must clean it up`` () =
        task {
            do! setupBatchTest ()
            let user = Tg.user(id = 7710L, username = "crash_finalize", firstName = "Crash")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let! _ =
                fixture.Execute(
                    """
                    INSERT INTO "user"(id, username, first_name, created_at, updated_at)
                    VALUES (@u, 'crashuser', 'Crash', NOW(), NOW())
                    ON CONFLICT (id) DO NOTHING;
                    """, {| u = user.Id |})

            // Simulate the post-crash state: batch flipped to awaiting_user but
            // bulk_message_id is still NULL because SendMessage never landed.
            // Insert with updated_at = bot's FakeTimeProvider epoch (fixedUtcNow)
            // so a later AdvanceBotClock(>1h) makes the row TTL-stale relative
            // to the bot's clock (the reap uses bot_now, not Postgres NOW()).
            let mgid = $"mg-crash-{DateTime.UtcNow.Ticks}"
            let botEpoch = fixture.FixedUtcNow.UtcDateTime
            let! batchId =
                fixture.QuerySingle<int64>(
                    """
                    INSERT INTO pending_add_batch(
                        user_id, media_group_id, bulk_chat_id, status,
                        bulk_message_id, created_at, updated_at)
                    VALUES (@u, @mg, @u, 'awaiting_user', NULL, @t, @t)
                    RETURNING id;
                    """, {| u = user.Id; mg = mgid; t = botEpoch |})

            // Item is already 'ok' — finalize had processed it.
            do! fixture.SetTelegramFile("crash-1", readImageBytes goodFile)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson goodFile)
            let! _ =
                fixture.Execute(
                    """
                    INSERT INTO pending_add_batch_item(
                        batch_id, seq, photo_file_id, photo_message_id, status,
                        value, min_check, expires_at, barcode_text)
                    VALUES (@b, 1, 'crash-1', 9801, 'ok', 10, 50, '2027-01-26', '2706688198845');
                    """, {| b = batchId |})

            do! fixture.ClearFakeCalls()
            do! fixture.RestartBotApp()

            // Either:
            //   (a) recovery re-sends bulk-confirm → user sees a "Подтвердить"
            //       message in chat AND batch.bulk_message_id is now non-NULL, OR
            //   (b) TTL reaps the stale awaiting_user batch on the next clock
            //       advance past 1h.

            // Give recovery up to 3s to act (BatchRecoveryService runs in StartAsync).
            do! Task.Delay 3000
            let! sendsAfterRecovery = fixture.GetFakeCalls("sendMessage")
            let recoveredBulkConfirm = (bulkConfirmCalls sendsAfterRecovery user.Id).Length > 0

            // Try the TTL path: advance well past 1h and see if reaper fires
            // on the next CreateBatchAtomically (we trigger one by sending a
            // new album from a DIFFERENT user — CreateBatchAtomically's
            // housekeeping DELETE is unscoped on user_id).
            do! fixture.AdvanceBotClock(60 * 60 * 1000 + 60_000) // 1h + 1min
            // Triggering another user's CreateBatchAtomically to invoke the reap.
            let triggerUser = Tg.user(id = 7711L, username = "trigger_reap", firstName = "Trig")
            do! fixture.SetChatMemberStatus(triggerUser.Id, "member")
            do! fixture.SetTelegramFile("trig-1", readImageBytes goodFile)
            let triggerMgid = $"mg-trig-{DateTime.UtcNow.Ticks}"
            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(triggerUser, triggerMgid, fileId = "trig-1", messageId = 9802))
            do! Task.Delay 500

            let! stillExists =
                fixture.QuerySingle<int64>(
                    "SELECT COUNT(*)::bigint FROM pending_add_batch WHERE id=@b",
                    {| b = batchId |})

            let cleanedUp = recoveredBulkConfirm || stillExists = 0L
            Assert.True(
                cleanedUp,
                $"Stuck awaiting_user batch was not cleaned up. recoveredBulkConfirm={recoveredBulkConfirm}, stillExists={stillExists}. " +
                "Either recovery should re-render the bulk-confirm for awaiting_user batches with bulk_message_id=NULL, " +
                "or the TTL housekeeping in CreateBatchAtomically should also reap stale awaiting_user rows.")
        }
