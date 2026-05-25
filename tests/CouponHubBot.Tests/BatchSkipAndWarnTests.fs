namespace CouponHubBot.Tests

open System
open System.Threading.Tasks
open BotTestInfra
open Xunit
open FakeCallHelpers
open BatchTestHelpers

/// Per-photo reply scenarios — when OCR fails to extract a barcode for some or
/// all photos in an album, the bot sends individual reply messages targeting
/// the specific photo's message_id with a reason-specific text.
type BatchSkipAndWarnTests(fixture: OcrCouponHubTestContainers) =

    let setupBatchTest () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            do! fixture.TruncateBatches()
            do! resetOcrFakes fixture
        }

    /// Sends an album where one photo deliberately yields no barcode (by stripping
    /// the 13-digit barcode line from Azure's response and using an image whose
    /// ZXing barcode scan also fails).
    [<Fact>]
    let ``1 of 3 photos has no barcode: bulk-confirm shows N-1, one per-photo reply targets the failed photo`` () =
        task {
            do! setupBatchTest ()

            let user = Tg.user(id = 7100L, username = "one_no_barcode", firstName = "Skip")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-skip1-{DateTime.UtcNow.Ticks}"

            // Two good photos + one image with NO barcode (we use an empty-byte
            // image; ZXing will fail and Azure response is stripped).
            let goodA = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            let goodB = "10_50_2026-01-17_2026-01-26_2706688198838.jpg"

            do! fixture.SetTelegramFile("skip-good-1", readImageBytes goodA)
            do! fixture.SetTelegramFile("skip-good-2", readImageBytes goodB)
            // Empty bytes → ZXing fails to decode any barcode.
            do! fixture.SetTelegramFile("skip-bad-1", [| 0uy; 1uy; 2uy; 3uy |])

            // Azure response strips the barcode line → text fallback also fails.
            let strippedAzure = stripBarcodeFromAzureJson (readAzureCacheJson goodA)
            do! fixture.SetAzureOcrResponse(200, strippedAzure)

            // Send 3 album photos with EXPLICIT messageIds we can assert against.
            let mid1 = 9001
            let mid2 = 9002
            let mid3 = 9003
            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = "skip-good-1", messageId = mid1))
            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = "skip-bad-1",  messageId = mid2))
            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = "skip-good-2", messageId = mid3))

            let! batchId = waitForBatchByUser fixture user.Id 5000
            do! waitForAllItemsTerminal fixture batchId 10000

            // Verify the bad item ended up needs_input BEFORE finalize fires (so
            // we know finalize will see it as needs_input, not 'pending').
            let! badStatus =
                fixture.QuerySingle<string>(
                    "SELECT status FROM pending_add_batch_item WHERE batch_id=@b AND photo_file_id='skip-bad-1'",
                    {| b = batchId |})
            Assert.Equal("needs_input", badStatus)

            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchId "awaiting_user" 5000
            do! waitForBulkConfirmCall fixture user.Id 5000
            // SendPerPhotoReplies runs after the bulk-confirm send; poll for it.
            do! waitForReplyCount fixture user.Id "распознать" 1 5000

            let! calls = fixture.GetFakeCalls("sendMessage")

            // Bulk-confirm advertises 2 ok items (the bad one is skipped from header).
            Assert.True(findCallWithText calls user.Id "Подтвердить 2 купонов",
                        "Expected 'Подтвердить 2 купонов' header")

            // Exactly one per-photo reply with reply_to targeting the BAD photo's mid.
            let replies = replyCalls calls user.Id (Some "распознать")
            Assert.Equal(1, replies.Length)
            let replyToMid = getReplyToMessageId replies[0].Body
            Assert.Equal(Some mid2, replyToMid)
        }

    [<Fact>]
    let ``2 of 5 photos no barcode: two per-photo replies, each targeting its own message_id`` () =
        task {
            do! setupBatchTest ()

            let user = Tg.user(id = 7101L, username = "two_no_barcode", firstName = "Skip2")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-skip2-{DateTime.UtcNow.Ticks}"

            let goodA = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            do! fixture.SetTelegramFile("s2-good-1", readImageBytes goodA)
            do! fixture.SetTelegramFile("s2-good-2", readImageBytes "10_50_2026-01-17_2026-01-26_2706688198838.jpg")
            do! fixture.SetTelegramFile("s2-good-3", readImageBytes "10_50_2026-01-17_2026-01-26_2706688198821.jpg")
            do! fixture.SetTelegramFile("s2-bad-1",  [| 0uy; 1uy; 2uy |])
            do! fixture.SetTelegramFile("s2-bad-2",  [| 9uy; 8uy; 7uy |])

            let strippedAzure = stripBarcodeFromAzureJson (readAzureCacheJson goodA)
            do! fixture.SetAzureOcrResponse(200, strippedAzure)

            let badMids = [ 9102; 9104 ]
            let files = [
                "s2-good-1", 9101
                "s2-bad-1",  9102
                "s2-good-2", 9103
                "s2-bad-2",  9104
                "s2-good-3", 9105
            ]
            for fid, mid in files do
                let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = fid, messageId = mid))
                ()

            let! batchId = waitForBatchByUser fixture user.Id 5000
            do! waitForAllItemsTerminal fixture batchId 15000

            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchId "awaiting_user" 5000
            do! waitForBulkConfirmCall fixture user.Id 5000
            // SendPerPhotoReplies runs after the bulk-confirm send; poll for it.
            do! waitForReplyCount fixture user.Id "распознать" 2 5000

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls user.Id "Подтвердить 3 купонов",
                        "Expected 'Подтвердить 3 купонов' header for 3 ok + 2 bad")

            let replies = replyCalls calls user.Id (Some "распознать")
            Assert.Equal(2, replies.Length)
            let replyMids =
                replies
                |> Array.choose (fun c -> getReplyToMessageId c.Body)
                |> Array.sort
            Assert.Equal<int list>(List.sort badMids, Array.toList replyMids)
        }

    [<Fact>]
    let ``All photos fail OCR: 'Не смог распознать ни одного' bulk-confirm with only cancel button + per-photo replies`` () =
        task {
            do! setupBatchTest ()

            let user = Tg.user(id = 7102L, username = "all_fail", firstName = "AllFail")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-allfail-{DateTime.UtcNow.Ticks}"

            do! fixture.SetTelegramFile("af-1", [| 0uy; 1uy |])
            do! fixture.SetTelegramFile("af-2", [| 2uy; 3uy |])
            do! fixture.SetTelegramFile("af-3", [| 4uy; 5uy |])

            let goodA = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            let strippedAzure = stripBarcodeFromAzureJson (readAzureCacheJson goodA)
            do! fixture.SetAzureOcrResponse(200, strippedAzure)

            let mids = [ 9201; 9202; 9203 ]
            for (fid, mid) in List.zip ["af-1"; "af-2"; "af-3"] mids do
                let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = fid, messageId = mid))
                ()

            let! batchId = waitForBatchByUser fixture user.Id 5000
            do! waitForAllItemsTerminal fixture batchId 10000

            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchId "awaiting_user" 5000
            do! waitForBulkConfirmCall fixture user.Id 5000
            // SendPerPhotoReplies runs after the bulk-confirm send; poll for it.
            do! waitForReplyCount fixture user.Id "распознать" 3 5000

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls user.Id "Не смог распознать ни одного",
                        "Expected 'all-failed' bulk-confirm text")

            // Bulk-confirm should NOT contain "Подтвердить" (no confirm button)
            // — the keyboard should be cancel-only when okCount = 0.
            let bulks = bulkConfirmCalls calls user.Id
            Assert.Equal(1, bulks.Length)
            let bulkText =
                match parseCallBody bulks[0].Body with
                | Some p -> Option.defaultValue "" p.Text
                | None -> ""
            Assert.DoesNotContain("Подтвердить", bulkText)

            // 3 per-photo replies, one per photo.
            let replies = replyCalls calls user.Id (Some "распознать")
            Assert.Equal(3, replies.Length)
            let replyMids =
                replies
                |> Array.choose (fun c -> getReplyToMessageId c.Body)
                |> Array.sort
            Assert.Equal<int list>(List.sort mids, Array.toList replyMids)
        }

    [<Fact>]
    let ``Per-photo reply text varies by reason: timeout vs OCR failed vs no barcode`` () =
        task {
            // Three separate one-photo batches, each ending in a different needs_input
            // reason. Verify the reply wording matches what NeedsInputReplyText returns.
            //
            //   timeout      → "не успел обработаться"
            //   OCR failed   → "Не получилось распознать"
            //   no barcode   → "Не смог распознать этот купон" (the default text)
            do! setupBatchTest ()
            let user = Tg.user(id = 7103L, username = "reply_text", firstName = "ReplyText")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            // ── (1) timeout ──
            do! fixture.TruncateBatches()
            do! fixture.ClearFakeCalls()
            do! resetOcrFakes fixture
            let mgid1 = $"mg-rt-timeout-{DateTime.UtcNow.Ticks}"
            do! fixture.SetTelegramFile("rt-timeout", readImageBytes "10_50_2026-01-17_2026-01-26_2706688198845.jpg")
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson "10_50_2026-01-17_2026-01-26_2706688198845.jpg")
            do! fixture.SetAzureOcrDelay(1000)
            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid1, fileId = "rt-timeout", messageId = 9301))
            let! b1 = waitForBatchByUser fixture user.Id 5000
            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture b1 "awaiting_user" 5000
            do! waitForBulkConfirmCall fixture user.Id 5000
            do! waitForSendMessageMatching fixture user.Id (fun t -> t.Contains "не успел обработаться") 3000
            do! waitForAzureCallCount fixture 1 3000

            // ── (2) OCR failed (network errors on both attempts) ──
            do! setupBatchTest ()
            do! fixture.ClearAzureOcrCalls()
            let mgid2 = $"mg-rt-failed-{DateTime.UtcNow.Ticks}"
            do! fixture.SetTelegramFile("rt-failed", readImageBytes "10_50_2026-01-17_2026-01-26_2706688198845.jpg")
            do! fixture.SetAzureOcrErrorMode("network")
            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid2, fileId = "rt-failed", messageId = 9302))
            let! b2 = waitForBatchByUser fixture user.Id 5000
            do! waitForAllItemsTerminal fixture b2 10000
            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture b2 "awaiting_user" 5000
            do! waitForBulkConfirmCall fixture user.Id 5000
            do! waitForSendMessageMatching fixture user.Id (fun t -> t.Contains "Не получилось распознать") 3000

            // ── (3) no barcode (OCR completes but yields no barcode) ──
            do! setupBatchTest ()
            let mgid3 = $"mg-rt-nobc-{DateTime.UtcNow.Ticks}"
            do! fixture.SetTelegramFile("rt-nobc", [| 0uy; 1uy; 2uy |])
            let goodA = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            do! fixture.SetAzureOcrResponse(200, stripBarcodeFromAzureJson (readAzureCacheJson goodA))
            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid3, fileId = "rt-nobc", messageId = 9303))
            let! b3 = waitForBatchByUser fixture user.Id 5000
            do! waitForAllItemsTerminal fixture b3 10000
            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture b3 "awaiting_user" 5000
            do! waitForBulkConfirmCall fixture user.Id 5000
            do! waitForSendMessageMatching fixture user.Id (fun t -> t.Contains "Не смог распознать этот купон") 3000
        }

    // NOTE: "reply-fallback when reply target is gone" (P2 from the gap analysis)
    // is not tested here. The fake-tg's SetMethodError is all-or-nothing on
    // sendMessage, so it can't simulate "reply call fails, plain send succeeds"
    // without infra changes. Worth a separate task if we want the coverage.
