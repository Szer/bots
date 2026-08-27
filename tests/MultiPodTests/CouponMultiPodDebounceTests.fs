namespace MultiPodTests

open System
open System.Threading.Tasks
open BotTestInfra
open MultiPodTests.FakeCallHelpers
open Npgsql
open Dapper
open Xunit

/// Proves BatchDebounce's cross-pod fix: photos for ONE album arrive on alternating instances,
/// each arming its OWN timer -- only FinalizeBatch's DB-authoritative check decides when to finalize.
type CouponMultiPodDebounceTests(fixture: CouponMultiPodContainers) =

    let batchIdFor (conn: NpgsqlConnection) (userId: int64) =
        conn.QuerySingleAsync<int64>(
            "SELECT id FROM pending_add_batch WHERE user_id=@u ORDER BY id DESC LIMIT 1", {| u = userId |})

    let batchStatus (conn: NpgsqlConnection) (batchId: int64) =
        conn.QuerySingleAsync<string>("SELECT status FROM pending_add_batch WHERE id=@b", {| b = batchId |})

    let itemCount (conn: NpgsqlConnection) (batchId: int64) =
        conn.QuerySingleAsync<int64>(
            "SELECT COUNT(*)::bigint FROM pending_add_batch_item WHERE batch_id=@b", {| b = batchId |})

    [<Fact>]
    let ``Split album across two instances: no premature finalize, exactly one finalize with all photos`` () = task {
        do! fixture.ClearFakeCalls()
        use conn = new NpgsqlConnection(fixture.DbConnectionString)
        do! conn.OpenAsync()

        let user = Tg.user(id = 961001L, username = "split_album")
        do! fixture.SetChatMemberStatus(user.Id, "member")
        let mgid = $"mg-split-{DateTime.UtcNow.Ticks}"

        // Photo 1 on instance 0 — creates the batch and arms instance 0's timer for
        // BATCH_DEBOUNCE_MS (default 5000ms, unseeded by this fixture).
        let! resp1 = fixture.SendUpdateTo(0, Tg.dmAlbumPhoto(user, mgid, fileId = "split-1", messageId = 9701L))
        Assert.True(resp1.IsSuccessStatusCode)
        let! batchId = batchIdFor conn user.Id

        // Elapsed since photo 1 so far: 0ms. Sanity check, no timer has come due yet.
        do! fixture.AdvanceAllClocks(3000)
        do! Task.Delay 700
        let! status1 = batchStatus conn batchId
        Assert.Equal("open", status1)

        // Photo 2 on instance 1 (different pod) reuses the same batch and arms its OWN timer.
        let! resp2 = fixture.SendUpdateTo(1, Tg.dmAlbumPhoto(user, mgid, fileId = "split-2", messageId = 9702L))
        Assert.True(resp2.IsSuccessStatusCode)
        let! itemsAfter2 = itemCount conn batchId
        Assert.Equal(2L, itemsAfter2)

        // Elapsed since photo 1 is now past instance 0's 5000ms debounce -- without the
        // DB-authoritative check this would flip early; instead it re-reads updated_at and defers.
        do! fixture.AdvanceAllClocks(3000)
        do! Task.Delay 700
        let! status2 = batchStatus conn batchId
        Assert.Equal("open", status2)

        // Photo 3 on instance 0 — bumps updated_at again and re-arms instance 0 fresh.
        let! resp3 = fixture.SendUpdateTo(0, Tg.dmAlbumPhoto(user, mgid, fileId = "split-3", messageId = 9703L))
        Assert.True(resp3.IsSuccessStatusCode)
        let! itemsAfter3 = itemCount conn batchId
        Assert.Equal(3L, itemsAfter3)

        // Elapsed since photo 2's bump: ~3000ms — still under 5000ms, so instance 1's
        // re-armed remainder from its own earlier defer should ALSO still be deferring.
        do! fixture.AdvanceAllClocks(3000)
        do! Task.Delay 700
        let! status3 = batchStatus conn batchId
        Assert.Equal("open", status3)

        // Well past 5000ms since photo 3's bump -- both timers race TryFlipBatchToAwaiting, one wins.
        do! fixture.AdvanceAllClocks(6000)

        let deadline = DateTime.UtcNow.AddSeconds(15.0)
        let mutable finalStatus = status3
        while finalStatus <> "awaiting_user" && DateTime.UtcNow < deadline do
            let! s = batchStatus conn batchId
            finalStatus <- s
            if finalStatus <> "awaiting_user" then do! Task.Delay 200
        Assert.Equal("awaiting_user", finalStatus)

        let! finalItemCount = itemCount conn batchId
        Assert.Equal(3L, finalItemCount)

        // Exactly one bulk-confirm UI landed -- proves the single-winner held even though both
        // pods' timers came due together. DB write and Telegram send are separate steps, so poll.
        let deadline3 = DateTime.UtcNow.AddSeconds(5.0)
        let mutable confirmCount = 0
        while confirmCount = 0 && DateTime.UtcNow < deadline3 do
            let! calls = fixture.GetFakeCalls("sendMessage")
            confirmCount <- bulkConfirmCallCount calls user.Id
            if confirmCount = 0 then do! Task.Delay 200
        Assert.Equal(1, confirmCount)
    }

    /// A straggler photo lands on the instance that DIDN'T win the finalize race, after the batch
    /// flipped -- AddBatchItem's status='open' gate rejects it; the user must be notified, not dropped.
    [<Fact>]
    let ``Straggler photo on the non-finalizing instance after flip is rejected and user notified`` () = task {
        do! fixture.ClearFakeCalls()
        use conn = new NpgsqlConnection(fixture.DbConnectionString)
        do! conn.OpenAsync()

        let user = Tg.user(id = 961002L, username = "split_straggler")
        do! fixture.SetChatMemberStatus(user.Id, "member")
        let mgid = $"mg-straggle-{DateTime.UtcNow.Ticks}"

        let! _ = fixture.SendUpdateTo(0, Tg.dmAlbumPhoto(user, mgid, fileId = "straggle-1", messageId = 9711L))
        let! batchId = batchIdFor conn user.Id

        // Clear the debounce window so the batch finalizes (instance 0's timer).
        do! fixture.AdvanceAllClocks(6000)
        let deadline = DateTime.UtcNow.AddSeconds(15.0)
        let mutable status = "open"
        while status <> "awaiting_user" && DateTime.UtcNow < deadline do
            let! s = batchStatus conn batchId
            status <- s
            if status <> "awaiting_user" then do! Task.Delay 200
        Assert.Equal("awaiting_user", status)

        // Straggler on instance 1 (never touched this batch) after the flip -- still resolves
        // the same batch id (partial-unique-index window), but AddBatchItem must reject it.
        let! _ = fixture.SendUpdateTo(1, Tg.dmAlbumPhoto(user, mgid, fileId = "straggle-2", messageId = 9712L))

        let deadline2 = DateTime.UtcNow.AddSeconds(10.0)
        let mutable notified = false
        while not notified && DateTime.UtcNow < deadline2 do
            let! calls = fixture.GetFakeCalls("sendMessage")
            notified <- findCallWithText calls user.Id "не попал в текущий пакет"
            if not notified then do! Task.Delay 200
        Assert.True(notified, "Expected the cross-pod straggler to be rejected with a user notice")

        let! finalItemCount = itemCount conn batchId
        Assert.Equal(1L, finalItemCount)
    }
