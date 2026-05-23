namespace CouponHubBot.Tests

open System
open System.Threading.Tasks
open BotTestInfra
open Dapper
open Npgsql
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

    // ── Helpers for placeholder/abandonment accounting ─────────────────

    /// Count of calls (e.g. deleteMessage) addressed to `chatId`.
    let callsToChat (calls: FakeCall array) (chatId: int64) =
        calls
        |> Array.filter (fun call ->
            match parseCallBody call.Body with
            | Some p -> p.ChatId = Some chatId
            | None -> false)

    /// editMessageText calls to `chatId` whose text contains the abandonment
    /// marker the bot writes when superseding an old batch.
    let abandonmentEdits (edits: FakeCall array) (chatId: int64) =
        edits
        |> Array.filter (fun call ->
            match parseCallBody call.Body with
            | Some p when p.ChatId = Some chatId ->
                match p.Text with
                | Some t -> t.Contains("Отменено: пришёл новый альбом")
                | _ -> false
            | _ -> false)

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

            // Only one batch was created — concurrent CreateBatchAtomically races
            // for the same media_group_id are resolved by ON CONFLICT on the
            // UNIQUE partial index (user_id, media_group_id) WHERE status active.
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
            // Different images for each user so their barcodes differ — otherwise
            // the 2nd TryAddCoupon hits coupon_barcode_active_uniq (V13) and
            // returns DuplicateBarcode, dropping that user's count to 0.
            let fileA = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            let fileB = "10_50_2026-01-17_2026-01-26_2706688198838.jpg"
            do! fixture.SetTelegramFile("2u-a-1", readImageBytes fileA)
            do! fixture.SetTelegramFile("2u-b-1", readImageBytes fileB)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson fileA)

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
    let ``Two truly-concurrent albums (different mgid) from same user: exactly one survives`` () =
        task {
            do! setupBatchTest ()
            let user = Tg.user(id = 7320L, username = "two_albums_same", firstName = "TwoAlb")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgidA = $"mg-2alb-A-{DateTime.UtcNow.Ticks}"
            let mgidB = $"mg-2alb-B-{DateTime.UtcNow.Ticks + 1L}"
            do! fixture.SetTelegramFile("2a-A-1", readImageBytes goodFile)
            do! fixture.SetTelegramFile("2a-B-1", readImageBytes goodFile)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson goodFile)

            // Send both concurrently — different connections, sub-ms arrival in
            // the bot. Before the per-user advisory lock in CreateBatchAtomically,
            // both webhooks would race through "create batch + abandon others"
            // and each would delete the other's batch, leaving zero batches.
            // With the lock, exactly one survives (the second to enter the lock).
            let tasks = [
                fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgidA, fileId = "2a-A-1"))
                fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgidB, fileId = "2a-B-1"))
            ]
            let! _ = Task.WhenAll(tasks)
            do! Task.Delay 500

            let! totalCount =
                fixture.QuerySingle<int64>(
                    "SELECT COUNT(*)::bigint FROM pending_add_batch WHERE user_id=@u",
                    {| u = user.Id |})
            Assert.Equal(1L, totalCount)

            let! activeBatchCount =
                fixture.QuerySingle<int64>(
                    "SELECT COUNT(*)::bigint FROM pending_add_batch WHERE user_id=@u AND status IN ('open','awaiting_user')",
                    {| u = user.Id |})
            Assert.Equal(1L, activeBatchCount)
        }

    // ── New concurrency-gap tests ───────────────────────────────────────

    /// SAFETY INVARIANT: every "обрабатываю купоны" placeholder the bot sends
    /// must be accounted for — either edited to "Отменено: пришёл новый альбом"
    /// (the abandoned-batch path) or deleted by FinalizeBatch (the winning
    /// batch's path). An orphan placeholder is a UX leak: the user sees
    /// "обрабатываю купоны..." forever.
    ///
    /// DETERMINISTIC reproduction of the orphan race:
    ///
    /// 1. SetFakeTgMethodDelay("sendMessage", 800) — every sendMessage in this
    ///    test sleeps 800ms before responding.
    /// 2. Fire webhook A. HandleAlbumPhoto runs CreateBatchAtomically (commits
    ///    B1, releases advisory lock), then awaits SendMessage placeholder
    ///    → BLOCKED for 800ms.
    /// 3. After 200ms, fire webhook C. By now A has long released the advisory
    ///    lock; C acquires it, runs CreateBatchAtomically (insert B2, abandon
    ///    B1). B1's bulk_message_id is still NULL (A hasn't completed its
    ///    SendMessage yet) so the abandon edit is suppressed.
    /// 4. C also awaits SendMessage placeholder → also blocked 800ms.
    /// 5. Wait until both sendMessage delays clear and both webhooks finish.
    /// 6. SetBatchBulkMessageId(B1, M1) on A's side is a 0-row UPDATE (B1
    ///    already DELETEd) — silent. M1 is now an ORPHAN.
    /// 7. Advance debounce → FinalizeBatch for B2 deletes M2 and sends bulk-
    ///    confirm. NO finalize for B1 (deleted; timer's TryFlipBatchToAwaiting
    ///    returns false).
    ///
    /// Net: 2 placeholders sent, 0 abandonment edits, 1 deleteMessage. The
    /// invariant `placeholders <= abandoned + deleted` is `2 <= 0 + 1` → FAIL.
    /// Today this test fails every run; after the fix it should pass every run.
    [<Fact>]
    let ``Concurrent different-mgid albums: every placeholder is either edited or deleted (no orphan)`` () =
        task {
            do! setupBatchTest ()
            let user = Tg.user(id = 7340L, username = "orphan_check", firstName = "Orph")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let mgidA = $"mg-orph-A-{DateTime.UtcNow.Ticks}"
            let mgidB = $"mg-orph-B-{DateTime.UtcNow.Ticks + 1L}"
            do! fixture.SetTelegramFile("orph-A-1", readImageBytes goodFile)
            do! fixture.SetTelegramFile("orph-B-1", readImageBytes goodFile)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson goodFile)

            // Force a wide race window. 800ms is comfortably larger than any
            // realistic CreateBatchAtomically transaction + lock-acquisition
            // cost (which is sub-millisecond locally and a few ms in CI).
            do! fixture.SetFakeTgMethodDelay("sendMessage", 800)

            // Send webhook A; it'll block in SendMessage placeholder.
            let aTask = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgidA, fileId = "orph-A-1"))

            // Wait long enough that A has committed CreateBatchAtomically
            // (including its own getFile / DB round-trips up to the
            // SendMessage call) and is parked inside SendMessage.
            do! Task.Delay 200

            // Send webhook C. With A parked in SendMessage, C can sail
            // through CreateBatchAtomically uncontested and abandon B1
            // while bulk_message_id is still NULL — that's the bug.
            let cTask = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgidB, fileId = "orph-B-1"))

            let! _ = Task.WhenAll([ aTask; cTask ])

            // Clear the delay so subsequent operations (advancePastDebounce
            // → finalize's DeleteMessage + bulk-confirm SendMessage) run
            // at normal speed. setupBatchTest in any later test also clears
            // delays via ClearFakeCalls, so a crash here is self-healing.
            do! fixture.SetFakeTgMethodDelay("sendMessage", 0)

            // Wait for any in-flight OCR to land and for the surviving batch
            // to be in a state to finalize.
            do! Task.Delay 300
            do! advancePastDebounce fixture
            do! waitForBulkConfirmCall fixture user.Id 5000
            do! Task.Delay 200

            let! sends = fixture.GetFakeCalls("sendMessage")
            let! edits = fixture.GetFakeCalls("editMessageText")
            let! deletes = fixture.GetFakeCalls("deleteMessage")

            let placeholders = (placeholderCalls sends user.Id).Length
            let abandoned = (abandonmentEdits edits user.Id).Length
            let deleted = (callsToChat deletes user.Id).Length

            Assert.True(
                placeholders <= abandoned + deleted,
                $"Orphan placeholder leak: placeholders={placeholders}, abandoned-edits={abandoned}, deletes={deleted}")
        }

    /// Confirm and cancel callbacks arrive at the same instant from the same
    /// user on the same batch. The bot has no serialisation between
    /// BulkBatchConfirm and BulkBatchCancel — both load the batch, both think
    /// it's in awaiting_user, both act.
    ///
    /// No deterministic seam exists for this race (unlike Tests 1 and 3, where
    /// either FakeTg sendMessage delay or a side-connection advisory lock
    /// forces the interleaving). The race is between two callback handlers
    /// that interact only with the DB — both go through `BulkBatchConfirm` /
    /// `BulkBatchCancel` which are pure DB-call sequences with no network
    /// hook we can pause from the test side. So we loop the scenario N times,
    /// amortising container startup and DB setup over many iterations.
    ///
    /// Per-iteration setup is direct DB injection (no OCR pipeline) so each
    /// iteration is ~50–100ms; 100 iterations runs in ~10s total. With 100
    /// independent attempts the race is hit on essentially every test run if
    /// the bug exists.
    ///
    /// INVARIANT per iteration: (a) 0 or 1 coupon (never duplicates), AND
    /// (b) the user-facing message agrees with the actual coupon count.
    [<Fact>]
    let ``Confirm + cancel concurrent click on same batch: final state and final message agree (100x)`` () =
        task {
            do! setupBatchTest ()
            let user = Tg.user(id = 7350L, username = "confirm_cancel_race", firstName = "CC")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            // Ensure user row exists for the FK on pending_add_batch.user_id.
            let! _ =
                fixture.Execute(
                    """
                    INSERT INTO "user"(id, username, first_name, created_at, updated_at)
                    VALUES (@u, 'cc', 'CC', NOW(), NOW())
                    ON CONFLICT (id) DO NOTHING;
                    """, {| u = user.Id |})

            // Helper: directly inject an awaiting_user batch with one 'ok' item
            // ready to be inserted as a coupon on confirm. Returns batch id.
            // Each iteration uses a unique mgid, barcode, and photo_file_id so
            // confirm's TryAddCoupon never hits coupon_barcode_active_uniq
            // from a prior iteration's leftover coupon — that would mask the
            // race by always returning DuplicateBarcode.
            let injectAwaitingBatch (iter: int) =
                task {
                    let mgid = $"mg-cc-{iter}-{DateTime.UtcNow.Ticks}"
                    // Unique 13-digit barcode per iteration.
                    let barcode = $"99{iter:D11}"
                    let! batchId =
                        fixture.QuerySingle<int64>(
                            """
                            INSERT INTO pending_add_batch(user_id, media_group_id, bulk_chat_id, status, bulk_message_id)
                            VALUES (@u, @mg, @u, 'awaiting_user', 1000)
                            RETURNING id;
                            """, {| u = user.Id; mg = mgid |})
                    let! _ =
                        fixture.Execute(
                            """
                            INSERT INTO pending_add_batch_item(
                                batch_id, seq, photo_file_id, photo_message_id, status,
                                value, min_check, expires_at, barcode_text)
                            VALUES (@b, 1, @fid, 9000, 'ok', 10, 50, '2027-12-31', @bc);
                            """,
                            {| b = batchId; fid = $"cc-{iter}"; bc = barcode |})
                    return batchId
                }

            let mutable violations = 0
            let mutable raceHit = 0
            let mutable lastMsgMismatch = 0
            let violationDetails = System.Collections.Generic.List<string>()
            let iterations = 100

            for i in 1 .. iterations do
                let! batchId = injectAwaitingBatch i
                do! fixture.ClearFakeCalls()

                // Fire both callbacks at once.
                let tasks = [
                    fixture.SendUpdate(Tg.dmCallback($"addflow:bulk:confirm:{batchId}", user))
                    fixture.SendUpdate(Tg.dmCallback($"addflow:bulk:cancel:{batchId}", user))
                ]
                let! _ = Task.WhenAll(tasks)
                do! waitForBatchCleared fixture batchId 5000
                do! Task.Delay 100

                // Count the coupon inserted by THIS iteration (filter by barcode
                // since prior iterations' coupons stay in the table).
                let expectedBarcode = $"99{i:D11}"
                let! thisIterCount =
                    fixture.QuerySingle<int64>(
                        "SELECT COUNT(*)::bigint FROM coupon WHERE owner_id=@u AND barcode_text=@bc",
                        {| u = user.Id; bc = expectedBarcode |})

                let! sends = fixture.GetFakeCalls("sendMessage")
                let! edits = fixture.GetFakeCalls("editMessageText")
                let textsFrom (calls: FakeCall array) =
                    calls
                    |> Array.choose (fun c ->
                        match parseCallBody c.Body with
                        | Some p when p.ChatId = Some user.Id -> p.Text
                        | _ -> None)
                let allTexts = Array.append (textsFrom sends) (textsFrom edits)
                let sawAdded = allTexts |> Array.exists (fun t -> t.Contains "Добавил")
                let sawCancelMsg = allTexts |> Array.exists (fun t -> t.Contains "Ок, отменил пакет")
                let sawStale = allTexts |> Array.exists (fun t -> t.Contains "пакет уже устарел")
                let sawCancelOrStale = sawCancelMsg || sawStale

                // Race signature: BOTH BulkBatchConfirm and BulkBatchCancel reached
                // their EditBulkOrSend step on the live batch. If only one ran (the
                // other saw missing batch and returned "уже устарел"), they
                // serialized on the DB and there was no race.
                let raced = sawAdded && sawCancelMsg
                if raced then raceHit <- raceHit + 1

                // The user-visible final state is the LAST edit to the bulk
                // message id (Telegram applies edits in arrival order).
                let lastEditText =
                    edits
                    |> Array.filter (fun c ->
                        match parseCallBody c.Body with
                        | Some p -> p.ChatId = Some user.Id
                        | None -> false)
                    |> Array.sortByDescending (fun c -> c.Timestamp)
                    |> Array.tryHead
                    |> Option.bind (fun c ->
                        match parseCallBody c.Body with
                        | Some p -> p.Text
                        | None -> None)

                // Invariant (a): no duplicate coupons.
                if thisIterCount > 1L then
                    violations <- violations + 1
                    violationDetails.Add($"iter {i}: dup coupons (count={thisIterCount})")

                // Invariant (b): some message reached the user.
                if thisIterCount = 1L && not sawAdded then
                    violations <- violations + 1
                    violationDetails.Add($"iter {i}: 1 coupon added but no 'Добавил' message")
                if thisIterCount = 0L && not sawCancelOrStale then
                    violations <- violations + 1
                    violationDetails.Add($"iter {i}: 0 coupons but no cancellation/stale message")

                // Invariant (c): the LAST visible edit must agree with the actual
                // coupon outcome. This is the UX bug we're really hunting: when
                // confirm and cancel both run, the last edit determines what the
                // user sees. If a coupon was added but the user sees "Ок, отменил
                // пакет.", they've been lied to.
                match lastEditText, thisIterCount with
                | Some t, 1L when t.Contains "Ок, отменил пакет" ->
                    lastMsgMismatch <- lastMsgMismatch + 1
                    violations <- violations + 1
                    violationDetails.Add(
                        $"iter {i}: coupon added but final message is 'Ок, отменил пакет.' — UX lies to user about outcome")
                | Some t, 0L when t.Contains "Добавил" ->
                    lastMsgMismatch <- lastMsgMismatch + 1
                    violations <- violations + 1
                    violationDetails.Add(
                        $"iter {i}: no coupon added but final message is 'Добавил…' — UX lies")
                | _ -> ()

            // Always surface the diagnostic counts so a passing run still tells us
            // whether the race ever actually fired (otherwise a passing test could
            // just mean the bot serialised everything for some reason we missed).
            let summary =
                $"race-hit={raceHit}/{iterations}, last-message-mismatch={lastMsgMismatch}/{iterations}, violations={violations}"

            Assert.True(
                (violations = 0),
                summary + "\nFirst few: " +
                String.Join("\n", violationDetails |> Seq.truncate 10))

            // Also fail if the race NEVER fired — that would mean this test is
            // pointless (something is serialising the callbacks). We'd want to
            // know and either fix the test or accept that the race can't happen.
            Assert.True(
                raceHit > 0,
                $"Race never fired in {iterations} iterations ({summary}). " +
                "Either there's an unknown serialisation path between BulkBatchConfirm and BulkBatchCancel " +
                "(check ASP.NET request handling, Kestrel concurrency, or DB row-lock contention), " +
                "or this scenario does not race in practice and the test is not useful.")
        }

    /// Album webhook + /add command arrive at the same instant from the same
    /// user. /add's AbandonOpenBatchesExcept(user, None) does NOT take the
    /// per-user advisory lock — only CreateBatchAtomically does. So if /add's
    /// path runs ENTIRELY between album's ClearPendingAddFlow and album's
    /// CreateBatchAtomically (which is a sub-millisecond window in normal
    /// conditions), the user ends up with BOTH a pending_add row AND a
    /// pending_add_batch row.
    ///
    /// DETERMINISTIC reproduction:
    ///
    /// 1. Test opens a separate Postgres connection, BEGIN, executes
    ///    `SELECT pg_advisory_xact_lock(@u)`. This holds the per-user lock.
    /// 2. Fire album webhook. HandleAlbumPhoto runs ClearPendingAddFlow OK,
    ///    then reaches CreateBatchAtomically → blocks on `pg_advisory_xact_lock`
    ///    behind our held-lock connection.
    /// 3. Fire /add webhook. ClearPendingFeedback (no-op), no
    ///    ClearPendingAddFlow (special case for /add), AbandonOpenBatchesExcept
    ///    (no advisory lock taken — runs immediately, deletes 0 batches),
    ///    HandleAddWizardStart → UpsertPendingAddFlow (creates pending_add
    ///    row), sendText placeholder. /add completes.
    /// 4. Test COMMITs the blocker tx → advisory lock released.
    /// 5. Album resumes inside CreateBatchAtomically: housekeeping reap (no
    ///    matches), INSERT B1 (no conflict), abandon-others (none), commit.
    ///    Album proceeds to placeholder + AddBatchItem.
    /// 6. Final state: pending_add row exists (from /add), pending_add_batch
    ///    row exists (from album) — BOTH FLOWS ALIVE simultaneously, even
    ///    though one was "supposed to" cancel the other.
    ///
    /// The invariant `activeBatchCount + pendingAddCount <= 1` fails today
    /// every run. The fix (make AbandonOpenBatchesExcept also take the
    /// per-user advisory lock, or take the lock around the entire
    /// command-handling block in BotService.handlePrivateMessage) would let
    /// it pass.
    [<Fact>]
    let ``/add and album arriving concurrently: at most one of (batch, pending_add) survives`` () =
        task {
            do! setupBatchTest ()
            let user = Tg.user(id = 7360L, username = "add_album_race", firstName = "AAR")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            // Defensive: prior tests may have left pending_add rows.
            let! _ =
                fixture.Execute(
                    "DELETE FROM pending_add WHERE user_id=@u",
                    {| u = user.Id |})

            // Also ensure the user row exists — needed for the FK that
            // pending_add_batch.user_id references when the album webhook
            // eventually commits. The bot does UpsertUser before HandleAlbumPhoto,
            // but pre-seeding is harmless and makes the lock-holder transaction
            // self-contained.
            let! _ =
                fixture.Execute(
                    """
                    INSERT INTO "user"(id, username, first_name, created_at, updated_at)
                    VALUES (@u, 'aar', 'AAR', NOW(), NOW())
                    ON CONFLICT (id) DO NOTHING;
                    """, {| u = user.Id |})

            let mgid = $"mg-aar-{DateTime.UtcNow.Ticks}"
            do! fixture.SetTelegramFile("aar-1", readImageBytes goodFile)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson goodFile)

            // Hold the per-user advisory lock from a side connection so the
            // album webhook's CreateBatchAtomically parks inside Postgres
            // waiting for the lock. This widens the window between
            // ClearPendingAddFlow and CreateBatchAtomically commit from
            // sub-millisecond to as long as we want.
            use blockerConn = new NpgsqlConnection(fixture.DbConnectionString)
            do! blockerConn.OpenAsync()
            use blockerTx = blockerConn.BeginTransaction()
            let! _ =
                blockerConn.ExecuteAsync(
                    "SELECT pg_advisory_xact_lock(@u)",
                    {| u = user.Id |},
                    blockerTx)

            // Fire the album webhook — it'll block inside CreateBatchAtomically.
            let albumTask =
                fixture.SendUpdate(
                    Tg.dmAlbumPhoto(user, mgid, fileId = "aar-1", messageId = 9971))

            // Give album time to reach the advisory_xact_lock call.
            do! Task.Delay 300

            // Fire /add. With today's code: runs unimpeded since
            // AbandonOpenBatchesExcept and UpsertPendingAddFlow don't take the
            // lock — /add finishes inserting pending_add while album is still
            // parked. With a fix that has /add also acquire the per-user lock,
            // /add would block here.
            let! _ = fixture.SendUpdate(Tg.dmMessage("/add", user))

            // Give /add enough time to do its work (or to demonstrably block).
            // 500ms >> any DB op; if /add hasn't created a pending_add row by
            // now, it's because it's blocked behind the advisory lock (which
            // means the fix is in).
            do! Task.Delay 500

            // Release the lock — album resumes; any blocked /add also resumes.
            do! blockerTx.CommitAsync()
            do! blockerConn.CloseAsync()

            let! _ = albumTask
            do! Task.Delay 500

            let! activeBatchCount =
                fixture.QuerySingle<int64>(
                    "SELECT COUNT(*)::bigint FROM pending_add_batch WHERE user_id=@u AND status IN ('open','awaiting_user')",
                    {| u = user.Id |})
            let! pendingAddCount =
                fixture.QuerySingle<int64>(
                    "SELECT COUNT(*)::bigint FROM pending_add WHERE user_id=@u",
                    {| u = user.Id |})

            Assert.True(
                activeBatchCount + pendingAddCount <= 1L,
                $"Both flows survived simultaneously: batches={activeBatchCount}, pending_add={pendingAddCount}. " +
                "AbandonOpenBatchesExcept (called from /add) does not take the per-user advisory lock " +
                "that CreateBatchAtomically holds, so the two paths can interleave.")
        }

    /// Telegram occasionally redelivers webhook updates. After FinalizeBatch
    /// has flipped a batch to 'awaiting_user', a redelivery of the SAME album
    /// photo (same media_group_id, same photo_file_id, same message_id) must
    /// not:
    ///   - create a second item (ON CONFLICT (batch_id, photo_file_id) DO NOTHING)
    ///   - send a second placeholder
    ///   - re-trigger OCR
    [<Fact>]
    let ``Telegram redelivers album photo after finalize: no second item, no second placeholder`` () =
        task {
            do! setupBatchTest ()
            let user = Tg.user(id = 7370L, username = "redeliver", firstName = "Redel")
            do! fixture.SetChatMemberStatus(user.Id, "member")
            let mgid = $"mg-redel-{DateTime.UtcNow.Ticks}"
            do! fixture.SetTelegramFile("redel-1", readImageBytes goodFile)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson goodFile)

            // First delivery: standard path through to awaiting_user.
            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = "redel-1", messageId = 9981))
            let! batchId = waitForBatchByUser fixture user.Id 5000
            do! waitForAllItemsTerminal fixture batchId 10000
            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchId "awaiting_user" 5000
            do! waitForBulkConfirmCall fixture user.Id 5000

            do! fixture.ClearFakeCalls()
            // Redeliver: SAME photo_file_id, SAME mgid, SAME message_id.
            let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = "redel-1", messageId = 9981))
            do! Task.Delay 500

            // Item count must stay at 1 (ON CONFLICT photo_file_id).
            let! itemCount =
                fixture.QuerySingle<int64>(
                    "SELECT COUNT(*)::bigint FROM pending_add_batch_item WHERE batch_id=@b",
                    {| b = batchId |})
            Assert.Equal(1L, itemCount)

            // No NEW placeholder (the bot's CreateBatchAtomically returned
            // isNew=false because the batch already exists; HandleAlbumPhoto
            // gates the placeholder on isNew).
            let! sends = fixture.GetFakeCalls("sendMessage")
            Assert.Equal(0, (placeholderCalls sends user.Id).Length)
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
