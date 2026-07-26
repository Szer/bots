namespace CouponHubBot.RealTests

open System
open Xunit

/// Coverage: `/added` + its bare `void:<id>` button, the `myAdded` button (which renders
/// the same as `/added`, CallbackHandler.fs:429-431), and the text-command / bare-button
/// variants of take/used/return that CouponLifecycleRealTests.fs does NOT cover (that file
/// owns the `:del`-suffixed take-confirmation-card buttons and the plain `/list` + `take:`
/// flow — see its own doc comment).
///
/// Bare vs `:del` disambiguation: CallbackHandler.fs's "used:"/"return:"/"void:" branches
/// (CallbackHandler.fs:346-381) all match by `StartsWith`, so both the bare `used:<id>`
/// (rendered under `/my`, CommandHandler.fs:247-248) and the `:del`-suffixed
/// `used:<id>:del` (rendered on the take-confirmation card by BotHelpers.singleTakenKeyboard)
/// are valid presses of the SAME handler — only the trailing message-delete differs. Every
/// button lookup below therefore uses an EXACT match (`d = $"used:{couponId}"`, never
/// `d.StartsWith`) so it can never accidentally grab the `:del` sibling when both are
/// present on the same message (they are not, in practice — `/my` and the take card are
/// different messages — but an exact predicate makes that invariant load-bearing rather
/// than incidental).
///
/// Single shared MTProto account owns, takes and voids every coupon here (see
/// MembershipGateRealTests' doc comment) — DbService.TryTakeCoupon has no self-take
/// restriction and CommandHandler.fs:307-332's void path allows a non-admin to void their
/// OWN coupon, so no second account/admin setup is needed. Each test adds its own coupon
/// via a distinct fixture (no cross-test chaining — xUnit does not guarantee ordering).
type MyAndAddedRealTests(fx: RealAssemblyFixture) =

    [<Fact>]
    /// `/added` lists an active added coupon and its bare `void:<id>` button
    /// (CommandHandler.fs:267-305 / :301) voids it — confirmed via
    /// DbSeed.getCouponStatusAsync against the `voided` status CommandHandler.fs's
    /// handleVoid/DbService.VoidCoupon writes (DbService.fs:910).
    member _.``added lists the coupon and its void button voids it``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let! couponId = RealTestHelpers.addCouponViaCaptionAsync fx "10_50_01-14_01-23_2706658654210.jpg" "10" "50" None

            let! addedSentId = fx.UserClient.SendText(fx.BotChatId, "/added")
            let! addedMsg = fx.UserClient.AwaitTextContaining(fx.BotChatId, addedSentId, $"ID:{couponId}", TimeSpan.FromSeconds 60.)

            let voidData = fx.UserClient.FindCallbackData(addedMsg, fun d -> d = $"void:{couponId}")
            Assert.True(voidData.IsSome, $"Expected a void:{couponId} button on the /added message")

            do! fx.UserClient.PressCallbackButton(fx.BotChatId, addedMsg.id, voidData.Value)
            let! _voidedReply = fx.UserClient.AwaitTextContaining(fx.BotChatId, addedMsg.id, $"Купон ID:{couponId} аннулирован", TimeSpan.FromSeconds 60.)

            let! status = DbSeed.getCouponStatusAsync fx.DbConnectionString couponId
            Assert.Equal("voided", status.status)
        })

    [<Fact>]
    /// The `myAdded` button on `/my`'s bottom row (CommandHandler.fs:258) renders the same
    /// list `/added` does (CallbackHandler.fs:429-431 -> HandleAdded). Adds AND takes a
    /// coupon first so `/my` has a taken-coupon section to render (the myAdded button
    /// itself is unconditional, but this exercises the realistic non-empty state).
    member _.``myAdded button on /my renders the added-coupons list``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let! couponId = RealTestHelpers.addCouponViaCaptionAsync fx "10_50_01-19_01-28_2706613152454.jpg" "10" "50" None
            let! takeSentId = fx.UserClient.SendText(fx.BotChatId, $"/take {couponId}")
            let! _taken = fx.UserClient.AwaitPhotoCaptionContaining(fx.BotChatId, takeSentId, "теперь твой", TimeSpan.FromSeconds 60.)

            let! mySentId = fx.UserClient.SendText(fx.BotChatId, "/my")
            let! myMsg = fx.UserClient.AwaitTextContaining(fx.BotChatId, mySentId, $"Купон ID:{couponId}", TimeSpan.FromSeconds 60.)

            do! fx.UserClient.PressCallbackButtonMatching(fx.BotChatId, myMsg, (fun d -> d = "myAdded"), "myAdded")
            let! addedMsg = fx.UserClient.AwaitTextContaining(fx.BotChatId, myMsg.id, "Мои добавленные купоны", TimeSpan.FromSeconds 60.)
            Assert.Contains($"ID:{couponId}", addedMsg.message)
        })

    [<Fact>]
    /// The BARE `used:<id>` button under `/my` (CommandHandler.fs:247-248, distinct from
    /// the `:del`-suffixed take-confirmation-card button CouponLifecycleRealTests.fs
    /// covers) marks the coupon used and does NOT delete the `/my` message afterwards
    /// (CallbackHandler.fs:359-371 only deletes when `data.EndsWith(":del")`).
    member _.``bare used button under /my marks the coupon used``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let! couponId = RealTestHelpers.addCouponViaCaptionAsync fx "10_50_01-21_01-30_2706616470579.jpg" "10" "50" None
            let! takeSentId = fx.UserClient.SendText(fx.BotChatId, $"/take {couponId}")
            let! _taken = fx.UserClient.AwaitPhotoCaptionContaining(fx.BotChatId, takeSentId, "теперь твой", TimeSpan.FromSeconds 60.)

            let! mySentId = fx.UserClient.SendText(fx.BotChatId, "/my")
            let! myMsg = fx.UserClient.AwaitTextContaining(fx.BotChatId, mySentId, $"Купон ID:{couponId}", TimeSpan.FromSeconds 60.)

            let usedData = fx.UserClient.FindCallbackData(myMsg, fun d -> d = $"used:{couponId}")
            Assert.True(usedData.IsSome, $"Expected a bare used:{couponId} button under /my")

            do! fx.UserClient.PressCallbackButton(fx.BotChatId, myMsg.id, usedData.Value)
            let! _usedReply = fx.UserClient.AwaitTextContaining(fx.BotChatId, myMsg.id, $"Купон ID:{couponId} отмечен", TimeSpan.FromSeconds 60.)

            let! status = DbSeed.getCouponStatusAsync fx.DbConnectionString couponId
            Assert.Equal("used", status.status)
        })

    [<Fact>]
    /// The BARE `return:<id>` button under `/my` (CommandHandler.fs:247-248, distinct from
    /// the `:del`-suffixed take-confirmation-card button) returns the coupon to the
    /// available pool.
    member _.``bare return button under /my returns the coupon to available``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let! couponId = RealTestHelpers.addCouponViaCaptionAsync fx "5_25_01-15_01-21_2706528422291.jpg" "5" "25" None
            let! takeSentId = fx.UserClient.SendText(fx.BotChatId, $"/take {couponId}")
            let! _taken = fx.UserClient.AwaitPhotoCaptionContaining(fx.BotChatId, takeSentId, "теперь твой", TimeSpan.FromSeconds 60.)

            let! mySentId = fx.UserClient.SendText(fx.BotChatId, "/my")
            let! myMsg = fx.UserClient.AwaitTextContaining(fx.BotChatId, mySentId, $"Купон ID:{couponId}", TimeSpan.FromSeconds 60.)

            let returnData = fx.UserClient.FindCallbackData(myMsg, fun d -> d = $"return:{couponId}")
            Assert.True(returnData.IsSome, $"Expected a bare return:{couponId} button under /my")

            do! fx.UserClient.PressCallbackButton(fx.BotChatId, myMsg.id, returnData.Value)
            let! _returnedReply = fx.UserClient.AwaitTextContaining(fx.BotChatId, myMsg.id, $"Купон ID:{couponId} возвращён", TimeSpan.FromSeconds 60.)

            let! status = DbSeed.getCouponStatusAsync fx.DbConnectionString couponId
            Assert.Equal("available", status.status)
        })

    [<Fact>]
    /// Text command `/used <id>` (CommandHandler.fs:451-457 -> handleUsed,
    /// CommandHandler.fs:127-135) marks the coupon used.
    member _.``text command /used marks the coupon used``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let! couponId = RealTestHelpers.addCouponViaCaptionAsync fx "5_25_01-15_01-21_2706666377231.jpg" "5" "25" None
            let! takeSentId = fx.UserClient.SendText(fx.BotChatId, $"/take {couponId}")
            let! _taken = fx.UserClient.AwaitPhotoCaptionContaining(fx.BotChatId, takeSentId, "теперь твой", TimeSpan.FromSeconds 60.)

            let! usedSentId = fx.UserClient.SendText(fx.BotChatId, $"/used {couponId}")
            let! _usedReply = fx.UserClient.AwaitTextContaining(fx.BotChatId, usedSentId, $"Купон ID:{couponId} отмечен", TimeSpan.FromSeconds 60.)

            let! status = DbSeed.getCouponStatusAsync fx.DbConnectionString couponId
            Assert.Equal("used", status.status)
        })

    [<Fact>]
    /// Text command `/return <id>` (CommandHandler.fs:458-464 -> handleReturn,
    /// CommandHandler.fs:137-145) returns the coupon to the available pool.
    member _.``text command /return returns the coupon to available``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let! couponId = RealTestHelpers.addCouponViaCaptionAsync fx "5_25_01-15_01-21_2706684806638.jpg" "5" "25" None
            let! takeSentId = fx.UserClient.SendText(fx.BotChatId, $"/take {couponId}")
            let! _taken = fx.UserClient.AwaitPhotoCaptionContaining(fx.BotChatId, takeSentId, "теперь твой", TimeSpan.FromSeconds 60.)

            let! returnSentId = fx.UserClient.SendText(fx.BotChatId, $"/return {couponId}")
            let! _returnedReply = fx.UserClient.AwaitTextContaining(fx.BotChatId, returnSentId, $"Купон ID:{couponId} возвращён", TimeSpan.FromSeconds 60.)

            let! status = DbSeed.getCouponStatusAsync fx.DbConnectionString couponId
            Assert.Equal("available", status.status)
        })

    [<Fact>]
    /// Text command `/void <id>` (CommandHandler.fs:468-474 -> handleVoid,
    /// CommandHandler.fs:307-332) voids an added-but-not-taken coupon. Non-admins CAN
    /// void their own coupon (CommandHandler.fs:307-332 computes `isAdmin` only to decide
    /// whether to log an "admin voided someone else's coupon" line; the DB-level authz is
    /// `db.VoidCoupon(couponId, user.id, isAdmin)`, and this account is both owner and
    /// caller regardless of admin status).
    member _.``text command /void voids an added coupon``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let! couponId = RealTestHelpers.addCouponViaCaptionAsync fx "5_25_2026-02-08_2026-02-14_2706726228947.jpg" "5" "25" None

            let! voidSentId = fx.UserClient.SendText(fx.BotChatId, $"/void {couponId}")
            let! _voidedReply = fx.UserClient.AwaitTextContaining(fx.BotChatId, voidSentId, $"Купон ID:{couponId} аннулирован", TimeSpan.FromSeconds 60.)

            let! status = DbSeed.getCouponStatusAsync fx.DbConnectionString couponId
            Assert.Equal("voided", status.status)
        })
