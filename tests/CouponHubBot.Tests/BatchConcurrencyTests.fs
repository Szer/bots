namespace CouponHubBot.Tests

open System
open System.Threading.Tasks
open BotTestInfra
open Xunit
open FakeCallHelpers
open BatchTestHelpers

/// Concurrency tests: webhook updates arriving in parallel, two users
/// uploading simultaneously, two albums from the same user, large albums.
type BatchConcurrencyTests(fixture: OcrCouponHubTestContainers) =

    let setupBatchTest () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            do! fixture.TruncateBatches()
            do! resetOcrFakes fixture
        }

    let goodFile = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"

    [<Fact>]
    let ``3 concurrent SendUpdate calls for same album: exactly 3 items in one batch, no exceptions`` () =
        task {
            do! setupBatchTest ()
            let user = Tg.user(id = 7300L, username = "concurrent_album", firstName = "Conc")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-conc-{DateTime.UtcNow.Ticks}"
            let files = [
                "conc-1", "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
                "conc-2", "10_50_2026-01-17_2026-01-26_2706688198838.jpg"
                "conc-3", "10_50_2026-01-17_2026-01-26_2706688198821.jpg"
            ]
            for fid, fn in files do
                do! fixture.SetTelegramFile(fid, readImageBytes fn)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson (snd files[0]))

            // Fire all 3 SendUpdate calls in parallel.
            let updates = files |> List.map (fun (fid, _) -> Tg.dmAlbumPhoto(user, mgid, fileId = fid))
            let tasks = updates |> List.map (fun u -> fixture.SendUpdate(u))
            let! _ = Task.WhenAll(tasks)

            let! batchId = waitForBatchByUser fixture user.Id 5000
            do! waitForItemCount fixture batchId 3 10000
            do! waitForAllItemsTerminal fixture batchId 15000

            // Only one batch was created — concurrent CreateOrFindBatch races
            // are resolved by the ON CONFLICT/UNIQUE partial index.
            let! batchCount =
                fixture.QuerySingle<int64>(
                    "SELECT COUNT(*)::bigint FROM pending_add_batch WHERE user_id=@u",
                    {| u = user.Id |})
            Assert.Equal(1L, batchCount)

            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchId "awaiting_user" 5000
            do! waitForBulkConfirmCall fixture user.Id 5000

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.Equal(1, (placeholderCalls calls user.Id).Length)
            Assert.Equal(1, (bulkConfirmCalls calls user.Id).Length)
        }

    [<Fact>]
    let ``Two users uploading separate albums concurrently: independent batches, both confirmable`` () =
        task {
            do! setupBatchTest ()
            let userA = Tg.user(id = 7310L, username = "two_users_a", firstName = "A")
            let userB = Tg.user(id = 7311L, username = "two_users_b", firstName = "B")
            do! fixture.SetChatMemberStatus(userA.Id, "member")
            do! fixture.SetChatMemberStatus(userB.Id, "member")

            let mgidA = $"mg-2usrs-A-{DateTime.UtcNow.Ticks}"
            let mgidB = $"mg-2usrs-B-{DateTime.UtcNow.Ticks}"
            do! fixture.SetTelegramFile("2u-a-1", readImageBytes goodFile)
            do! fixture.SetTelegramFile("2u-b-1", readImageBytes goodFile)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson goodFile)

            // Send both concurrently.
            let tasks = [
                fixture.SendUpdate(Tg.dmAlbumPhoto(userA, mgidA, fileId = "2u-a-1"))
                fixture.SendUpdate(Tg.dmAlbumPhoto(userB, mgidB, fileId = "2u-b-1"))
            ]
            let! _ = Task.WhenAll(tasks)

            let! batchA = waitForBatchByUser fixture userA.Id 5000
            let! batchB = waitForBatchByUser fixture userB.Id 5000
            Assert.NotEqual(batchA, batchB)
            do! waitForAllItemsTerminal fixture batchA 10000
            do! waitForAllItemsTerminal fixture batchB 10000

            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchA "awaiting_user" 5000
            do! waitForBatchStatus fixture batchB "awaiting_user" 5000
            do! waitForBulkConfirmCall fixture userA.Id 5000
            do! waitForBulkConfirmCall fixture userB.Id 5000

            // Each user gets their own bulk-confirm and can confirm independently.
            let! _ = fixture.SendUpdate(Tg.dmCallback($"addflow:bulk:confirm:{batchA}", userA))
            let! _ = fixture.SendUpdate(Tg.dmCallback($"addflow:bulk:confirm:{batchB}", userB))
            do! waitForBatchCleared fixture batchA 5000
            do! waitForBatchCleared fixture batchB 5000

            let! countA = fixture.QuerySingle<int64>("SELECT COUNT(*)::bigint FROM coupon WHERE owner_id=@u", {| u = userA.Id |})
            let! countB = fixture.QuerySingle<int64>("SELECT COUNT(*)::bigint FROM coupon WHERE owner_id=@u", {| u = userB.Id |})
            Assert.Equal(1L, countA)
            Assert.Equal(1L, countB)
        }

    [<Fact>]
    let ``Two concurrent albums (different mgid) from same user: one wins, one is abandoned`` () =
        task {
            do! setupBatchTest ()
            let user = Tg.user(id = 7320L, username = "two_albums_same", firstName = "TwoAlb")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgidA = $"mg-2alb-A-{DateTime.UtcNow.Ticks}"
            let mgidB = $"mg-2alb-B-{DateTime.UtcNow.Ticks + 1L}"
            do! fixture.SetTelegramFile("2a-A-1", readImageBytes goodFile)
            do! fixture.SetTelegramFile("2a-B-1", readImageBytes goodFile)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson goodFile)

            // Send both concurrently.
            let tasks = [
                fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgidA, fileId = "2a-A-1"))
                fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgidB, fileId = "2a-B-1"))
            ]
            let! _ = Task.WhenAll(tasks)

            // Both batches initially get created (different media_group_id). Then
            // AbandonOpenBatchesExcept on the second arrival marks the first as
            // abandoned. Wait for the abandonment to settle.
            do! Task.Delay 500

            let! activeBatchCount =
                fixture.QuerySingle<int64>(
                    "SELECT COUNT(*)::bigint FROM pending_add_batch WHERE user_id=@u AND status IN ('open','awaiting_user')",
                    {| u = user.Id |})
            // Exactly one is in an active state; the other is abandoned.
            Assert.Equal(1L, activeBatchCount)

            let! abandonedCount =
                fixture.QuerySingle<int64>(
                    "SELECT COUNT(*)::bigint FROM pending_add_batch WHERE user_id=@u AND status='abandoned'",
                    {| u = user.Id |})
            Assert.Equal(1L, abandonedCount)
        }

    [<Fact>]
    let ``Album of 10 (stress): all 10 items processed, single batch, single bulk-confirm`` () =
        task {
            do! setupBatchTest ()
            let user = Tg.user(id = 7330L, username = "album_ten", firstName = "Ten")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-ten-{DateTime.UtcNow.Ticks}"
            // 10 distinct image files for unique barcodes.
            let availableFiles = [
                "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
                "10_50_2026-01-17_2026-01-26_2706688198838.jpg"
                "10_50_2026-01-17_2026-01-26_2706688198821.jpg"
                "10_50_2026-01-11_2026-01-20_2706678568818.jpg"
                "10_50_01-12_01-21_2706513420233.jpg"
                "10_50_01-12_01-21_2706530490622.jpg"
                "10_50_01-04_01-13_2706602781191.jpg"
                "10_50_01-06_01-15_2706643333717.jpg"
                "10_50_01-14_01-23_2706658654210.jpg"
                "10_50_01-19_01-28_2706613152454.jpg"
            ]
            for i, fn in List.indexed availableFiles do
                do! fixture.SetTelegramFile($"ten-{i}", readImageBytes fn)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson availableFiles[0])

            // Fire all 10 concurrent SendUpdate calls.
            let tasks =
                availableFiles
                |> List.mapi (fun i _ -> fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = $"ten-{i}")))
            let! _ = Task.WhenAll(tasks)

            let! batchId = waitForBatchByUser fixture user.Id 5000
            do! waitForItemCount fixture batchId 10 15000
            do! waitForAllItemsTerminal fixture batchId 30000

            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchId "awaiting_user" 5000
            do! waitForBulkConfirmCall fixture user.Id 5000

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.Equal(1, (placeholderCalls calls user.Id).Length)
            Assert.Equal(1, (bulkConfirmCalls calls user.Id).Length)
        }
