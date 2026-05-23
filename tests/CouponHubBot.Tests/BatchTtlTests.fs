namespace CouponHubBot.Tests

open System
open System.Threading.Tasks
open BotTestInfra
open Xunit
open FakeCallHelpers
open BatchTestHelpers

/// TTL-related tests for the album batch flow:
///   - CreateBatch reaps batches whose updated_at is > 1 hour old
///   - Stale awaiting_user confirm callback returns "пакет устарел"
type BatchTtlTests(fixture: OcrCouponHubTestContainers) =

    let setupBatchTest () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            do! fixture.TruncateBatches()
            do! resetOcrFakes fixture
        }

    let goodFile = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"

    [<Fact>]
    let ``Batch older than 1h is reaped on next album upload from the same user`` () =
        task {
            do! setupBatchTest ()
            let user = Tg.user(id = 7400L, username = "ttl_reap", firstName = "Ttl")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgidA = $"mg-ttlA-{DateTime.UtcNow.Ticks}"
            do! fixture.SetTelegramFile("ttl-A", readImageBytes goodFile)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson goodFile)
            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgidA, fileId = "ttl-A"))
            let! batchA = waitForBatchByUser fixture user.Id 5000

            // Force the batch into the "open" state (it is, since we didn't advance)
            // and age its updated_at past the 1-hour TTL.
            let! _ =
                fixture.Execute(
                    "UPDATE pending_add_batch SET updated_at = (now() - interval '61 minutes') WHERE id=@b",
                    {| b = batchA |})
            ()

            // Send a new album. CreateBatch's housekeeping DELETE should reap
            // the stale 'open' batch before inserting the new one.
            let mgidB = $"mg-ttlB-{DateTime.UtcNow.Ticks + 1L}"
            do! fixture.SetTelegramFile("ttl-B", readImageBytes goodFile)
            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgidB, fileId = "ttl-B"))

            // Wait a moment for the bot to process. Then query DB.
            do! Task.Delay 500

            let! batchAExists =
                fixture.QuerySingle<int64>(
                    "SELECT COUNT(*)::bigint FROM pending_add_batch WHERE id=@b",
                    {| b = batchA |})
            Assert.Equal(0L, batchAExists)

            // The new batch B exists.
            let! batchBCount =
                fixture.QuerySingle<int64>(
                    "SELECT COUNT(*)::bigint FROM pending_add_batch WHERE user_id=@u AND media_group_id=@mg",
                    {| u = user.Id; mg = mgidB |})
            Assert.Equal(1L, batchBCount)
        }

    [<Fact>]
    let ``Stale awaiting_user batch: confirm callback returns 'пакет устарел' even though row still exists`` () =
        task {
            do! setupBatchTest ()
            let user = Tg.user(id = 7401L, username = "stale_aw_callback", firstName = "StaleAw")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgid = $"mg-stale-aw-cb-{DateTime.UtcNow.Ticks}"
            do! fixture.SetTelegramFile("staw-1", readImageBytes goodFile)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson goodFile)
            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = "staw-1"))

            let! batchId = waitForBatchByUser fixture user.Id 5000
            do! waitForAllItemsTerminal fixture batchId 10000
            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchId "awaiting_user" 5000
            do! waitForBulkConfirmCall fixture user.Id 5000

            // Mutate the batch status to simulate something that "shouldn't be
            // confirmable any more". The cancel/confirm callback handler checks
            // status='awaiting_user'; anything else (or row missing) → "пакет
            // устарел". Easiest simulation: change status to something else.
            let! _ =
                fixture.Execute(
                    "UPDATE pending_add_batch SET status = 'abandoned' WHERE id=@b",
                    {| b = batchId |})
            ()

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback($"addflow:bulk:confirm:{batchId}", user))
            do! waitForSendMessageMatching fixture user.Id (fun t -> t.Contains "пакет уже устарел") 3000

            // No coupon was added (status wasn't awaiting_user).
            let! count = fixture.QuerySingle<int64>("SELECT COUNT(*)::bigint FROM coupon WHERE owner_id=@u", {| u = user.Id |})
            Assert.Equal(0L, count)
        }
