namespace CouponHubBot.Tests

open System
open System.Threading.Tasks
open BotTestInfra
open Xunit
open FakeCallHelpers
open BatchTestHelpers

/// Failure-mode tests: upstream OCR partially fails, or FinalizeBatch itself
/// throws. The bot must NEVER leave the user staring at the "Получил,
/// обрабатываю купоны…" placeholder — there must always be either a coherent
/// bulk-confirm or a fallback error message.
///
/// Caught a prod bug (smoke test): Azure OCR returned 403 (VNet config) for
/// every photo; ZXing still decoded the barcodes from clean coupon
/// screenshots; items were marked `status='ok'` with NULL value/min/date;
/// FormatBatchItemLine crashed on `item.value.Value`; FinalizeBatch had no
/// try/catch, so the user was stuck.
type BatchResilienceTests(fixture: OcrCouponHubTestContainers) =

    let setupBatchTest () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            do! fixture.TruncateBatches()
            do! resetOcrFakes fixture
        }

    /// Three real coupon screenshots: ZXing decodes the barcodes fine, but
    /// Azure OCR is down (403). Pre-fix: items got `status='ok'` with NULL
    /// money/date → FormatBatchItemLine threw → user stuck on placeholder.
    /// Post-fix: items go to `needs_input` with note="partial", bulk-confirm
    /// shows "Не смог распознать ни одного", N per-photo "partial" replies.
    [<Fact>]
    let ``Azure OCR returns 403 for all photos but ZXing decodes barcodes: items go to needs_input, no crash, partial replies`` () =
        task {
            do! setupBatchTest ()

            let user = Tg.user(id = 7300L, username = "azure_403", firstName = "AzureDown")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-azure403-{DateTime.UtcNow.Ticks}"

            // Three real coupon screenshots — ZXing will decode each barcode.
            let goodA = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            let goodB = "10_50_2026-01-17_2026-01-26_2706688198838.jpg"
            let goodC = "10_50_2026-01-17_2026-01-26_2706688198821.jpg"
            do! fixture.SetTelegramFile("a403-1", readImageBytes goodA)
            do! fixture.SetTelegramFile("a403-2", readImageBytes goodB)
            do! fixture.SetTelegramFile("a403-3", readImageBytes goodC)

            // Azure rejects every call (mirrors the VNet-blocked prod scenario).
            do! fixture.SetAzureOcrResponse(
                403,
                """{"error":{"code":"403","message":"A Virtual Network is configured for this resource."}}""")

            let mids = [ 9401; 9402; 9403 ]
            for (fid, mid) in List.zip ["a403-1"; "a403-2"; "a403-3"] mids do
                let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = fid, messageId = mid))
                ()

            let! batchId = waitForBatchByUser fixture user.Id 5000
            do! waitForAllItemsTerminal fixture batchId 10000

            // Every item should land as needs_input (no item is 'ok' since money/date
            // are NULL). Verify BEFORE finalize so the bug is caught upstream.
            let! okCount =
                fixture.QuerySingle<int64>(
                    "SELECT COUNT(*)::bigint FROM pending_add_batch_item WHERE batch_id=@b AND status='ok'",
                    {| b = batchId |})
            Assert.Equal(0L, okCount)
            let! needsInputCount =
                fixture.QuerySingle<int64>(
                    "SELECT COUNT(*)::bigint FROM pending_add_batch_item WHERE batch_id=@b AND status='needs_input'",
                    {| b = batchId |})
            Assert.Equal(3L, needsInputCount)
            let! partialCount =
                fixture.QuerySingle<int64>(
                    "SELECT COUNT(*)::bigint FROM pending_add_batch_item WHERE batch_id=@b AND failure_note='partial'",
                    {| b = batchId |})
            Assert.Equal(3L, partialCount)

            // FinalizeBatch must complete without crashing.
            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchId "awaiting_user" 5000
            do! waitForBulkConfirmCall fixture user.Id 5000

            let! calls = fixture.GetFakeCalls("sendMessage")

            // Bulk-confirm: "all failed" text, no confirm button.
            Assert.True(findCallWithText calls user.Id "Не смог распознать ни одного",
                        "Expected 'all-failed' bulk-confirm text")
            let bulks = bulkConfirmCalls calls user.Id
            Assert.Equal(1, bulks.Length)

            // 3 per-photo replies with the "partial" wording (barcode known,
            // money/date missing). NOT the generic "Не смог распознать" wording.
            let partialReplies =
                replyCalls calls user.Id (Some "Распознал штрихкод, но не разобрал")
            Assert.Equal(3, partialReplies.Length)
            let replyMids =
                partialReplies
                |> Array.choose (fun c -> getReplyToMessageId c.Body)
                |> Array.sort
            Assert.Equal<int list>(List.sort mids, Array.toList replyMids)
        }

    /// Same shape as above but Azure returns 200 with an empty body. Same
    /// outcome: barcode from ZXing, no money/date from text → "partial".
    /// Belt-and-suspenders with the 403 case so a future change to error
    /// handling in AzureBotOcr can't silently regress one path.
    [<Fact>]
    let ``Azure OCR returns 200 with empty body: items go to needs_input "partial", no crash`` () =
        task {
            do! setupBatchTest ()

            let user = Tg.user(id = 7301L, username = "azure_empty", firstName = "EmptyBody")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-azure-empty-{DateTime.UtcNow.Ticks}"

            let goodA = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            do! fixture.SetTelegramFile("ae-1", readImageBytes goodA)

            do! fixture.SetAzureOcrResponse(200, "{}")

            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = "ae-1", messageId = 9501))
            let! batchId = waitForBatchByUser fixture user.Id 5000
            do! waitForAllItemsTerminal fixture batchId 10000

            let! partialCount =
                fixture.QuerySingle<int64>(
                    "SELECT COUNT(*)::bigint FROM pending_add_batch_item WHERE batch_id=@b AND status='needs_input' AND failure_note='partial'",
                    {| b = batchId |})
            Assert.Equal(1L, partialCount)

            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchId "awaiting_user" 5000
            do! waitForBulkConfirmCall fixture user.Id 5000

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls user.Id "Не смог распознать ни одного",
                        "Expected 'all-failed' bulk-confirm text")
            let partialReplies =
                replyCalls calls user.Id (Some "Распознал штрихкод, но не разобрал")
            Assert.Equal(1, partialReplies.Length)
        }

    /// Regression guard for the broader class of bug: ANY exception inside
    /// FinalizeBatch's render/send pipeline must not leave the user stuck.
    /// Simulates a corrupt row (status='ok' but value=NULL — exactly what the
    /// prod bug produced) and asserts:
    ///   (a) a user-visible fallback message is sent / edited in
    ///   (b) the batch row is cleared so the user can retry immediately with
    ///       the same media_group_id
    [<Fact>]
    let ``FinalizeBatch render crash: user sees fallback, batch is cleared`` () =
        task {
            do! setupBatchTest ()

            let user = Tg.user(id = 7302L, username = "render_crash", firstName = "Crash")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-crash-{DateTime.UtcNow.Ticks}"

            // One real coupon screenshot — OCR will complete fine; we corrupt
            // the row by hand afterwards.
            let goodA = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            do! setupGoodOcr fixture "crash-1" goodA

            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = "crash-1", messageId = 9601))
            let! batchId = waitForBatchByUser fixture user.Id 5000
            do! waitForAllItemsTerminal fixture batchId 10000

            // Sanity: item is 'ok' with non-null fields.
            let! okFields =
                fixture.QuerySingle<int64>(
                    "SELECT COUNT(*)::bigint FROM pending_add_batch_item WHERE batch_id=@b AND status='ok' AND value IS NOT NULL",
                    {| b = batchId |})
            Assert.Equal(1L, okFields)

            // Capture the placeholder message id so we can assert an edit
            // targeting it later (the fallback path edits the placeholder).
            let! placeholderId =
                fixture.QuerySingle<int>(
                    "SELECT COALESCE(bulk_message_id, 0) FROM pending_add_batch WHERE id=@b",
                    {| b = batchId |})
            Assert.NotEqual(0, placeholderId)

            // Corrupt: set value to NULL while keeping status='ok'. RenderBulkConfirm
            // will call FormatBatchItemLine → item.value.Value → InvalidOperationException.
            let! _ =
                fixture.Execute(
                    "UPDATE pending_add_batch_item SET value=NULL WHERE batch_id=@b",
                    {| b = batchId |})

            // Fire the debounce. With the fallback in place, this must NOT
            // bubble the exception — the bot logs it and recovers.
            do! advancePastDebounce fixture

            // Wait for either:
            //   (a) editMessageText on the placeholder with the fallback text, OR
            //   (b) a fresh sendMessage with the fallback text
            // depending on which branch of the fallback fired.
            do! waitForSendMessageOrEditMatching
                    fixture
                    user.Id
                    (fun t -> t.Contains "Что-то пошло не так при обработке альбома")
                    5000

            // Batch must be cleared so the user can immediately retry with the
            // same media_group_id (the partial unique index would otherwise
            // block re-upload).
            do! waitForBatchCleared fixture batchId 5000

            // The bulk-confirm with "Подтвердить" must NOT have been sent —
            // the render path crashed before SendMessage.
            let! sendCalls = fixture.GetFakeCalls("sendMessage")
            Assert.False(findCallWithText sendCalls user.Id "Подтвердить",
                         "Bulk-confirm must NOT have been sent when render crashed")
        }
