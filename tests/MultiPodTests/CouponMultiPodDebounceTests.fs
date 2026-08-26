namespace MultiPodTests

open System
open System.Threading.Tasks
open BotTestInfra
open MultiPodTests.FakeCallHelpers
open Npgsql
open Dapper
open Xunit

/// Proves BatchDebounce's cross-pod fix at the process level: photos for ONE album
/// arrive on alternating instances, each arming its OWN in-memory timer. Only the
/// DB-authoritative check in FinalizeBatch (CouponFlowHandler.fs) — not the timers
/// themselves — decides when to actually finalize. Clocks only ever move via
/// AdvanceAllClocks (lockstep); a single-instance clock move would desync the two
/// FakeTimeProviders that FinalizeBatch's elapsedMs math compares against each
/// other's DB-persisted `updated_at`.
///
/// OCR is disabled on this fixture (CouponMultiPodContainers), so every item lands
/// on 'needs_input' near-instantly with no external HTTP call — irrelevant to what's
/// under test here (the BATCH-level flip timing, not per-item OCR outcome), and it
/// keeps the timeline free of OCR-latency noise.
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

        // Photo 2 on instance 1 (a DIFFERENT process/pod) — same media_group_id, so
        // CreateBatchAtomically reuses the same batch. AddBatchItem's touchSql bumps
        // updated_at using instance 1's OWN (lockstep) clock, and instance 1 arms its
        // OWN separate in-memory timer for the same batchId.
        let! resp2 = fixture.SendUpdateTo(1, Tg.dmAlbumPhoto(user, mgid, fileId = "split-2", messageId = 9702L))
        Assert.True(resp2.IsSuccessStatusCode)
        let! itemsAfter2 = itemCount conn batchId
        Assert.Equal(2L, itemsAfter2)

        // Elapsed since photo 1: 3000+3000=6000ms — PAST the 5000ms debounce that
        // instance 0's ORIGINAL timer (armed at photo 1) was counting down. Without
        // the DB-authoritative check this would flip the batch right here. With it,
        // instance 0 re-reads updated_at (bumped by photo 2 on instance 1 ~3000ms
        // ago) and defers instead.
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

        // Clear the quiet period for real (well past 5000ms since photo 3's bump) —
        // both instances' outstanding timers should now find the window genuinely
        // elapsed and race TryFlipBatchToAwaiting; exactly one wins.
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

        // Exactly one finalize's worth of bulk-confirm UI landed in the (shared)
        // FakeTgApi log — proves TryFlipBatchToAwaiting's single-winner held even
        // though BOTH pods' independently-armed timers came due around the same
        // simulated moment.
        let! calls = fixture.GetFakeCalls("sendMessage")
        Assert.Equal(1, bulkConfirmCallCount calls user.Id)
    }

    /// Optional extension of the split-album scenario: a straggler photo for the
    /// SAME media_group_id lands on the instance that DIDN'T win the finalize race,
    /// after the batch has already flipped. AddBatchItem's status='open' gate
    /// (DbService.fs) rejects it regardless of which pod re-used the batch id —
    /// the user must be notified, not silently dropped.
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

        // Straggler arrives on instance 1 (never touched this batch before) after
        // the flip — CreateBatchAtomically still resolves the same batch id (still
        // 'awaiting_user', within its partial-unique-index window), but AddBatchItem
        // must reject it.
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
