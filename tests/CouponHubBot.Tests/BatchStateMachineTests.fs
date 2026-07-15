namespace CouponHubBot.Tests

open System
open System.Threading.Tasks
open BotTestInfra
open Xunit
open FakeCallHelpers
open BatchTestHelpers

/// State-machine transition tests for the album batch flow:
///   Open → AwaitingUser → [*]  (confirm / cancel / supersede / command / TTL / stale)
///   Open → AwaitingUser → AwaitingUser  (late straggler re-arms debounce)
type BatchStateMachineTests(fixture: OcrCouponHubTestContainers) =

    let setupBatchTest () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            do! fixture.TruncateBatches()
            do! resetOcrFakes fixture
        }

    let goodFile = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"

    let sendOnePhotoAlbum (user: Funogram.Telegram.Types.User) (mgid: string) (fid: string) (messageId: int64) =
        task {
            do! fixture.SetTelegramFile(fid, readImageBytes goodFile)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson goodFile)
            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = fid, messageId = messageId))
            return ()
        }

    // ── Cancel paths ────────────────────────────────────────────────────

    [<Fact>]
    let ``Cancel callback while awaiting_user: batch cleared, message edited to 'Ок, пакет отменён.'`` () =
        task {
            do! setupBatchTest ()
            let user = Tg.user(id = 7200L, username = "cancel_aw", firstName = "Cancel")
            do! fixture.SetChatMemberStatus(user.Id, "member")
            let mgid = $"mg-cancel-aw-{DateTime.UtcNow.Ticks}"
            do! sendOnePhotoAlbum user mgid "cancel-aw-1" 9501

            let! batchId = waitForBatchByUser fixture user.Id 5000
            do! waitForAllItemsTerminal fixture batchId 10000
            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchId "awaiting_user" 5000
            do! waitForBulkConfirmCall fixture user.Id 5000

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback($"addflow:bulk:cancel:{batchId}", user))
            do! waitForBatchCleared fixture batchId 5000

            // The bulk-confirm message is edited to the cancel summary.
            do! waitForSendMessageOrEditMatching fixture user.Id (fun t -> t.Contains "Ок, пакет отменён") 3000
        }

    [<Fact>]
    let ``Confirm callback on missing batch returns 'пакет устарел'`` () =
        task {
            do! setupBatchTest ()
            let user = Tg.user(id = 7201L, username = "stale_confirm", firstName = "Stale")
            do! fixture.SetChatMemberStatus(user.Id, "member")
            let mgid = $"mg-stale-c-{DateTime.UtcNow.Ticks}"
            do! sendOnePhotoAlbum user mgid "stale-c-1" 9601

            let! batchId = waitForBatchByUser fixture user.Id 5000
            do! waitForAllItemsTerminal fixture batchId 10000
            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchId "awaiting_user" 5000

            // Delete the batch out from under the callback to simulate a stale
            // bulk-confirm message that the user clicks much later.
            let! _ = fixture.Execute("DELETE FROM pending_add_batch WHERE id=@b", {| b = batchId |})

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback($"addflow:bulk:confirm:{batchId}", user))

            do! waitForSendMessageMatching fixture user.Id (fun t -> t.Contains "пакет уже устарел") 3000
        }

    [<Fact>]
    let ``Cancel callback on missing batch returns 'пакет устарел'`` () =
        task {
            do! setupBatchTest ()
            let user = Tg.user(id = 7202L, username = "stale_cancel", firstName = "StaleCancel")
            do! fixture.SetChatMemberStatus(user.Id, "member")
            let mgid = $"mg-stale-x-{DateTime.UtcNow.Ticks}"
            do! sendOnePhotoAlbum user mgid "stale-x-1" 9701

            let! batchId = waitForBatchByUser fixture user.Id 5000
            do! waitForAllItemsTerminal fixture batchId 10000
            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchId "awaiting_user" 5000

            let! _ = fixture.Execute("DELETE FROM pending_add_batch WHERE id=@b", {| b = batchId |})

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback($"addflow:bulk:cancel:{batchId}", user))

            do! waitForSendMessageMatching fixture user.Id (fun t -> t.Contains "пакет уже устарел") 3000
        }

    [<Fact>]
    let ``Idempotent double-tap confirm: second click sees missing batch`` () =
        task {
            do! setupBatchTest ()
            let user = Tg.user(id = 7203L, username = "double_tap", firstName = "Double")
            do! fixture.SetChatMemberStatus(user.Id, "member")
            let mgid = $"mg-dtap-{DateTime.UtcNow.Ticks}"
            do! sendOnePhotoAlbum user mgid "dtap-1" 9801

            let! batchId = waitForBatchByUser fixture user.Id 5000
            do! waitForAllItemsTerminal fixture batchId 10000
            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchId "awaiting_user" 5000
            do! waitForBulkConfirmCall fixture user.Id 5000

            // First confirm — should succeed and clear the batch.
            let! _ = fixture.SendUpdate(Tg.dmCallback($"addflow:bulk:confirm:{batchId}", user))
            do! waitForBatchCleared fixture batchId 5000

            do! fixture.ClearFakeCalls()
            // Second confirm — batch is gone; should answer "пакет устарел".
            let! _ = fixture.SendUpdate(Tg.dmCallback($"addflow:bulk:confirm:{batchId}", user))
            do! waitForSendMessageMatching fixture user.Id (fun t -> t.Contains "пакет уже устарел") 3000

            // Coupon was inserted exactly ONCE (no duplicate from the second click).
            let! count = fixture.QuerySingle<int64>("SELECT COUNT(*)::bigint FROM coupon WHERE owner_id=@u", {| u = user.Id |})
            Assert.Equal(1L, count)
        }

    // ── New-album supersedes ────────────────────────────────────────────

    [<Fact>]
    let ``New album (different mgid) while previous open: old deleted, message edited`` () =
        task {
            do! setupBatchTest ()
            let user = Tg.user(id = 7210L, username = "supersede_open", firstName = "Super")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            // First album — create batch but DO NOT advance, so it stays 'open'.
            let mgidA = $"mg-sA-{DateTime.UtcNow.Ticks}"
            do! sendOnePhotoAlbum user mgidA "sA-1" 9901
            let! batchA = waitForBatchByUser fixture user.Id 5000

            // Second album with different media_group_id → bot's HandleAlbumPhoto
            // calls AbandonOpenBatchesExcept which DELETEs the older batch
            // (see DbService.fs:982 — DELETE … RETURNING *).
            let mgidB = $"mg-sB-{DateTime.UtcNow.Ticks + 1L}"
            do! sendOnePhotoAlbum user mgidB "sB-1" 9902

            // batchA should be gone, not merely 'abandoned'.
            do! waitForBatchCleared fixture batchA 5000

            // The old bulk message gets edited to "Отменено: пришёл новый альбом."
            do! waitForSendMessageOrEditMatching fixture user.Id (fun t -> t.Contains "Отменено: пришёл новый альбом") 5000

            // Verify the new batch is open and distinct.
            let! batchB =
                fixture.QuerySingle<int64>(
                    "SELECT id FROM pending_add_batch WHERE user_id=@u AND media_group_id=@mg",
                    {| u = user.Id; mg = mgidB |})
            Assert.NotEqual(batchA, batchB)
        }

    [<Fact>]
    let ``New album while previous in awaiting_user: old deleted too`` () =
        task {
            do! setupBatchTest ()
            let user = Tg.user(id = 7211L, username = "supersede_aw", firstName = "SuperAw")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgidA = $"mg-sAw-A-{DateTime.UtcNow.Ticks}"
            do! sendOnePhotoAlbum user mgidA "sAw-1" 9910
            let! batchA = waitForBatchByUser fixture user.Id 5000
            do! waitForAllItemsTerminal fixture batchA 10000
            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchA "awaiting_user" 5000
            do! waitForBulkConfirmCall fixture user.Id 5000

            // Now send a second album.
            let mgidB = $"mg-sAw-B-{DateTime.UtcNow.Ticks + 1L}"
            do! sendOnePhotoAlbum user mgidB "sAw-2" 9911

            // AbandonOpenBatchesExcept DELETEs the old batch, even from
            // 'awaiting_user' state. (The SQL filter is status IN ('open','awaiting_user').)
            do! waitForBatchCleared fixture batchA 5000
            do! waitForSendMessageOrEditMatching fixture user.Id (fun t -> t.Contains "Отменено: пришёл новый альбом") 5000
        }

    // ── Single-photo wizard preemption ──────────────────────────────────

    [<Fact>]
    let ``Single-photo pending_add active, then album arrives: single wizard is cleared`` () =
        task {
            do! setupBatchTest ()
            // Also clear any pending_add for this user.
            let user = Tg.user(id = 7220L, username = "wizard_preempt", firstName = "WPre")
            do! fixture.SetChatMemberStatus(user.Id, "member")
            let! _ = fixture.Execute("DELETE FROM pending_add WHERE user_id=@u", {| u = user.Id |})

            // Start a single-photo wizard with /add.
            let! _ = fixture.SendUpdate(Tg.dmMessage("/add", user))
            do! waitForPendingAddStage fixture user.Id "awaiting_photo" 3000

            // Now send an album. HandleAlbumPhoto should clear the pending_add row.
            let mgid = $"mg-wp-{DateTime.UtcNow.Ticks}"
            do! sendOnePhotoAlbum user mgid "wp-1" 9920

            // pending_add should be gone now (cleared by HandleAlbumPhoto).
            do! waitForPendingAddRowGone fixture user.Id 5000
            // And a batch should exist.
            let! _ = waitForBatchByUser fixture user.Id 5000
            return ()
        }

    // ── Commands cancel batches ─────────────────────────────────────────

    let assertCommandCancelsBatch (commandText: string) (uid: int64) (username: string) =
        task {
            do! setupBatchTest ()
            let user = Tg.user(id = uid, username = username, firstName = "Cmd")
            do! fixture.SetChatMemberStatus(user.Id, "member")
            let mgid = $"mg-{username}-{DateTime.UtcNow.Ticks}"
            do! sendOnePhotoAlbum user mgid $"{username}-1" 9930

            // Send the command BEFORE advancing — batch is in 'open'.
            let! _ = fixture.SendUpdate(Tg.dmMessage(commandText, user))

            let! batches =
                fixture.QuerySingle<int64>(
                    "SELECT COUNT(*)::bigint FROM pending_add_batch WHERE user_id=@u",
                    {| u = user.Id |})
            Assert.Equal(0L, batches)
        }

    [<Fact>]
    let ``/add command during open batch deletes the batch`` () =
        assertCommandCancelsBatch "/add" 7230L "cmd_add"

    [<Fact>]
    let ``/my command during open batch deletes the batch`` () =
        assertCommandCancelsBatch "/my" 7231L "cmd_my"

    [<Fact>]
    let ``/stats command during open batch deletes the batch`` () =
        assertCommandCancelsBatch "/stats" 7232L "cmd_stats"

    [<Fact>]
    let ``/feedback command during open batch deletes the batch`` () =
        assertCommandCancelsBatch "/feedback" 7233L "cmd_feedback"

    [<Fact>]
    let ``/add command during awaiting_user batch deletes the batch`` () =
        task {
            do! setupBatchTest ()
            let user = Tg.user(id = 7234L, username = "add_after_aw", firstName = "AddAw")
            do! fixture.SetChatMemberStatus(user.Id, "member")
            let mgid = $"mg-add-aw-{DateTime.UtcNow.Ticks}"
            do! sendOnePhotoAlbum user mgid "add-aw-1" 9940

            let! batchId = waitForBatchByUser fixture user.Id 5000
            do! waitForAllItemsTerminal fixture batchId 10000
            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchId "awaiting_user" 5000

            let! _ = fixture.SendUpdate(Tg.dmMessage("/add", user))
            do! waitForBatchCleared fixture batchId 5000
        }

    // ── Late straggler in awaiting_user ─────────────────────────────────

    [<Fact>]
    let ``Late straggler in awaiting_user appends an item and re-arms debounce`` () =
        task {
            do! setupBatchTest ()
            let user = Tg.user(id = 7240L, username = "late_straggler", firstName = "Late")
            do! fixture.SetChatMemberStatus(user.Id, "member")
            let mgid = $"mg-late-{DateTime.UtcNow.Ticks}"

            do! sendOnePhotoAlbum user mgid "late-1" 9950
            let! batchId = waitForBatchByUser fixture user.Id 5000
            do! waitForAllItemsTerminal fixture batchId 10000
            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchId "awaiting_user" 5000
            do! waitForBulkConfirmCall fixture user.Id 5000

            // Send another photo with the SAME media_group_id while batch is in
            // awaiting_user. AddBatchItem (which selects FOR UPDATE on
            // pending_add_batch rows in ('open','awaiting_user')) should accept it.
            do! fixture.SetTelegramFile("late-2", readImageBytes goodFile)
            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = "late-2", messageId = 9951))

            // Wait for the new item to appear and reach a terminal status (OCR).
            do! waitForItemCount fixture batchId 2 5000
            do! waitForAllItemsTerminal fixture batchId 10000

            // Fire debounce again → finalize re-runs.
            do! advancePastDebounce fixture
            do! Task.Delay 500

            // CURRENT BEHAVIOR (documenting the UX gap):
            // FinalizeBatch's first step is TryFlipBatchToAwaiting, which
            // updates WHERE status='open'. The batch is already 'awaiting_user'
            // from the first finalize, so this UPDATE matches 0 rows and
            // returns false → FinalizeBatch early-returns without re-rendering
            // the bulk-confirm message. The user's chat still shows
            // "Подтвердить 1 купон" even though 2 items now sit in
            // status='ok'.
            //
            // The leaked late item IS silently included when the user clicks
            // confirm — BulkBatchConfirm reads all items with status='ok' from
            // DB, not the snapshot rendered into the message. So the user is
            // told "1" but gets "2" coupons on confirm. UX surprise but no
            // data loss.
            //
            // If finalize is changed to re-render on late stragglers, tighten
            // these assertions to (== 2, == 2) — that's the more honest UX.
            let! calls = fixture.GetFakeCalls("sendMessage")
            let bulks = bulkConfirmCalls calls user.Id
            Assert.Equal(1, bulks.Length)

            let! itemCount =
                fixture.QuerySingle<int64>(
                    "SELECT COUNT(*)::bigint FROM pending_add_batch_item WHERE batch_id=@b",
                    {| b = batchId |})
            Assert.Equal(2L, itemCount)

            // Confirm DOES include both items in the actual coupon insert,
            // proving the leaked-but-silently-included UX gap.
            let! _ = fixture.SendUpdate(Tg.dmCallback($"addflow:bulk:confirm:{batchId}", user))
            do! waitForBatchCleared fixture batchId 5000
            let! couponCount =
                fixture.QuerySingle<int64>(
                    "SELECT COUNT(*)::bigint FROM coupon WHERE owner_id=@u",
                    {| u = user.Id |})
            // 1 coupon, not 2, because both items share the SAME barcode (goodFile)
            // and coupon_barcode_active_uniq (V13) dedupes on confirm. The straggler
            // is marked 'failed' as DuplicateBarcode. But it WAS attempted — the
            // confirm loop included it. If the two items had different barcodes,
            // both would have been inserted despite the message saying "1".
            Assert.Equal(1L, couponCount)
        }
