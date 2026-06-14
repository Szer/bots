namespace CouponHubBot.Tests

open BotTestInfra
open System.Net
open Dapper
open Npgsql
open Xunit
open FakeCallHelpers

type UndoFlowTests(fixture: DefaultCouponHubTestContainers) =

    // Admin user ID 900 is configured in FEEDBACK_ADMINS for the test container.
    let adminId = 900L

    let getLatestCouponId () =
        task {
            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            //language=postgresql
            return! conn.QuerySingleAsync<int>("SELECT id FROM coupon ORDER BY id DESC LIMIT 1")
        }

    let getStatus (couponId: int) =
        fixture.QuerySingle<string>("SELECT status FROM coupon WHERE id = @id", {| id = couponId |})

    // 0 when taken_by is NULL.
    let getTakenBy (couponId: int) =
        fixture.QuerySingle<int64>("SELECT COALESCE(taken_by, 0) FROM coupon WHERE id = @id", {| id = couponId |})

    let getEventCount (couponId: int) (eventType: string) =
        fixture.QuerySingle<int64>(
            "SELECT COUNT(*)::bigint FROM coupon_event WHERE coupon_id = @id AND event_type = @t",
            {| id = couponId; t = eventType |})

    // Finds the admin-facing reply that carries the <pre> history block.
    let findHistoryReply (calls: FakeCall array) =
        calls |> Array.tryPick (fun call ->
            match parseCallBody call.Body with
            | Some parsed when parsed.ChatId = Some adminId && parsed.Text.IsSome && parsed.Text.Value.Contains("<pre>") ->
                Some parsed.Text.Value
            | _ -> None)

    [<Fact>]
    let ``Undo used returns coupon to holder's pocket and reports history`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 720L, username = "undo_used_owner", firstName = "Owner")
            let taker = Tg.user(id = 721L, username = "undo_used_taker", firstName = "Taker")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")
            do! fixture.SetChatMemberStatus(adminId, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/used {couponId}", taker))

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage($"/undo {couponId}", Tg.user(id = adminId, username = "admin", firstName = "Admin")))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            // status rolled back, holder still has it
            let! status = getStatus couponId
            Assert.Equal("taken", status)
            let! takenBy = getTakenBy couponId
            Assert.Equal(taker.Id, takenBy)

            // compensating event appended, original not deleted
            let! used = getEventCount couponId "used"
            let! usedReverted = getEventCount couponId "used_reverted"
            Assert.Equal(1L, used)
            Assert.Equal(1L, usedReverted)

            // admin reply carries state line + full history table incl. the revert
            let! calls = fixture.GetFakeCalls("sendMessage")
            let history = findHistoryReply calls
            Assert.True(history.IsSome, "Admin should receive undo reply with <pre> history block")
            Assert.Contains("Откат купона", history.Value)
            Assert.Contains("used_reverted", history.Value)

            // coupon shows up again in the taker's /my
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmMessage("/my", taker))
            let! myCalls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText myCalls taker.Id $"ID:{couponId}" || findCallWithText myCalls taker.Id (string couponId),
                "Undone coupon should be back in the holder's /my")
        }

    [<Fact>]
    let ``Undo taken sends coupon back to the common pool`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 722L, username = "undo_taken_owner", firstName = "Owner")
            let taker = Tg.user(id = 723L, username = "undo_taken_taker", firstName = "Taker")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")
            do! fixture.SetChatMemberStatus(adminId, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))

            let! _ = fixture.SendUpdate(Tg.dmMessage($"/undo {couponId}", Tg.user(id = adminId, username = "admin", firstName = "Admin")))

            let! status = getStatus couponId
            Assert.Equal("available", status)
            let! takenBy = getTakenBy couponId
            Assert.Equal(0L, takenBy)
            let! takenReverted = getEventCount couponId "taken_reverted"
            Assert.Equal(1L, takenReverted)
        }

    [<Fact>]
    let ``Undo returned puts the coupon back in the holder's pocket`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 724L, username = "undo_ret_owner", firstName = "Owner")
            let taker = Tg.user(id = 725L, username = "undo_ret_taker", firstName = "Taker")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")
            do! fixture.SetChatMemberStatus(adminId, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/return {couponId}", taker))

            let! _ = fixture.SendUpdate(Tg.dmMessage($"/undo {couponId}", Tg.user(id = adminId, username = "admin", firstName = "Admin")))

            let! status = getStatus couponId
            Assert.Equal("taken", status)
            let! takenBy = getTakenBy couponId
            Assert.Equal(taker.Id, takenBy)
            let! returnedReverted = getEventCount couponId "returned_reverted"
            Assert.Equal(1L, returnedReverted)
        }

    [<Fact>]
    let ``Undo voided restores coupon to available pool`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 726L, username = "undo_void_owner", firstName = "Owner")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(adminId, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/void {couponId}", owner))

            let! _ = fixture.SendUpdate(Tg.dmMessage($"/undo {couponId}", Tg.user(id = adminId, username = "admin", firstName = "Admin")))

            let! status = getStatus couponId
            Assert.Equal("available", status)
            let! takenBy = getTakenBy couponId
            Assert.Equal(0L, takenBy)
            let! voidedReverted = getEventCount couponId "voided_reverted"
            Assert.Equal(1L, voidedReverted)
        }

    [<Fact>]
    let ``Undo of added is refused and points to void`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 727L, username = "undo_added_owner", firstName = "Owner")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(adminId, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/undo {couponId}", Tg.user(id = adminId, username = "admin", firstName = "Admin")))

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls adminId "/void",
                "Refusal should point the admin at /void")
            let! status = getStatus couponId
            Assert.Equal("available", status)
        }

    [<Fact>]
    let ``Non-admin undo is silently ignored and changes nothing`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 728L, username = "undo_na_owner", firstName = "Owner")
            let taker = Tg.user(id = 729L, username = "undo_na_taker", firstName = "Taker")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/used {couponId}", taker))

            do! fixture.ClearFakeCalls()
            // taker is not an admin
            let! resp = fixture.SendUpdate(Tg.dmMessage($"/undo {couponId}", taker))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.Equal(0, calls.Length)
            let! status = getStatus couponId
            Assert.Equal("used", status)
        }

    [<Fact>]
    let ``Two undos in a row peel back two steps to the common pool`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 730L, username = "undo_multi_owner", firstName = "Owner")
            let taker = Tg.user(id = 731L, username = "undo_multi_taker", firstName = "Taker")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")
            do! fixture.SetChatMemberStatus(adminId, "member")
            let admin = Tg.user(id = adminId, username = "admin", firstName = "Admin")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/used {couponId}", taker))

            // step 1: used -> taken
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/undo {couponId}", admin))
            let! s1 = getStatus couponId
            Assert.Equal("taken", s1)

            // step 2: taken -> available (the "два шага назад в общую копилку" case)
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/undo {couponId}", admin))
            let! s2 = getStatus couponId
            Assert.Equal("available", s2)
            let! takenBy = getTakenBy couponId
            Assert.Equal(0L, takenBy)

            // step 3: only 'added' remains live -> refused, state unchanged
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/undo {couponId}", admin))
            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls adminId "/void", "Third undo should hit the 'added' floor and refuse")
            let! s3 = getStatus couponId
            Assert.Equal("available", s3)
        }

    [<Fact>]
    let ``Undoing a use nets it out of the holder's stats`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 732L, username = "undo_stats_owner", firstName = "Owner")
            let taker = Tg.user(id = 733L, username = "undo_stats_taker", firstName = "Taker")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")
            do! fixture.SetChatMemberStatus(adminId, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/used {couponId}", taker))
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/undo {couponId}", Tg.user(id = adminId, username = "admin", firstName = "Admin")))

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmMessage("/stats", taker))
            let! calls = fixture.GetFakeCalls("sendMessage")
            // 'used' netted to 0, but the 'taken' (not reverted) still counts
            Assert.True(findCallWithText calls taker.Id "Использовано: 0",
                "Reverted use should net out of the event stats line")
            Assert.True(findCallWithText calls taker.Id "Взято: 1",
                "Non-reverted take should remain counted")
        }

    [<Fact>]
    let ``Undo on a coupon with zero events reports nothing to undo`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 740L, username = "undo_noevt_owner", firstName = "Owner")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(adminId, "member")

            // /add creates the user row (and an 'added' event on its own coupon)…
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            // …then insert a second coupon directly, with NO coupon_event rows.
            let! _ =
                fixture.Execute(
                    "INSERT INTO coupon (owner_id, photo_file_id, value, min_check, expires_at, status) VALUES (@o, @p, 7.00, 35.00, '2026-01-25'::date, 'available')",
                    {| o = owner.Id; p = "no-events-photo" |})
            let! couponId = getLatestCouponId ()
            let! evCount = getEventCount couponId "added"
            Assert.Equal(0L, evCount) // sanity: this coupon really has no events

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/undo {couponId}", Tg.user(id = adminId, username = "admin", firstName = "Admin")))

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls adminId "нет действий для отката",
                "Coupon with zero events should report nothing to undo")
            let! status = getStatus couponId
            Assert.Equal("available", status)
        }

    [<Fact>]
    let ``Undo on a non-existent coupon reports not found`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            do! fixture.SetChatMemberStatus(adminId, "member")

            let! _ = fixture.SendUpdate(Tg.dmMessage("/undo 999999", Tg.user(id = adminId, username = "admin", firstName = "Admin")))

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls adminId "не найден",
                "Undo on a missing coupon should report not found")
        }

    [<Fact>]
    let ``Deep chain peels through every event type and never redoes a reverted action`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 741L, username = "undo_chain_owner", firstName = "Owner")
            let taker = Tg.user(id = 742L, username = "undo_chain_taker", firstName = "Taker")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")
            do! fixture.SetChatMemberStatus(adminId, "member")
            let admin = Tg.user(id = adminId, username = "admin", firstName = "Admin")

            // Mirrors coupon 1057: added → taken → returned → taken → used
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/return {couponId}", taker))
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/used {couponId}", taker))

            let undoOnce () = fixture.SendUpdate(Tg.dmMessage($"/undo {couponId}", admin))

            // Peel back one live action at a time.
            let! _ = undoOnce ()      // used      → taken
            let! s1 = getStatus couponId
            Assert.Equal("taken", s1)

            let! _ = undoOnce ()      // taken (#2)→ available
            let! s2 = getStatus couponId
            Assert.Equal("available", s2)

            let! _ = undoOnce ()      // returned  → taken (holder restored)
            let! s3 = getStatus couponId
            Assert.Equal("taken", s3)
            let! takenBy3 = getTakenBy couponId
            Assert.Equal(taker.Id, takenBy3)

            let! _ = undoOnce ()      // taken (#1)→ available
            let! s4 = getStatus couponId
            Assert.Equal("available", s4)

            // Only 'added' remains live → refused, state unchanged.
            do! fixture.ClearFakeCalls()
            let! _ = undoOnce ()
            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls adminId "/void", "Floor at 'added' should refuse")
            let! s5 = getStatus couponId
            Assert.Equal("available", s5)

            // Crucially: reverts only ever appended compensations — no action was ever redone.
            let! added = getEventCount couponId "added"
            let! taken = getEventCount couponId "taken"
            let! returned = getEventCount couponId "returned"
            let! used = getEventCount couponId "used"
            let! takenRev = getEventCount couponId "taken_reverted"
            let! returnedRev = getEventCount couponId "returned_reverted"
            let! usedRev = getEventCount couponId "used_reverted"
            let! addedRev = getEventCount couponId "added_reverted"
            Assert.Equal(1L, added)
            Assert.Equal(2L, taken)        // original two takes, never re-applied
            Assert.Equal(1L, returned)
            Assert.Equal(1L, used)
            Assert.Equal(2L, takenRev)     // each take reverted exactly once
            Assert.Equal(1L, returnedRev)
            Assert.Equal(1L, usedRev)
            Assert.Equal(0L, addedRev)     // 'added' was refused, never compensated
        }
