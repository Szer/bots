namespace CouponHubBot.RealTests

open System
open Xunit

/// Covers the button-driven `/report` flow — `/my` -> `report` -> `report:<id>`
/// (coupon selection) -> `report:<id>:confirm`, `reportCancel` at both the select and
/// confirm steps, and the adder-side `reportedUsed:<id>` acknowledgement
/// (CallbackHandler.fs's `data = "report"` / `data.StartsWith "report:"` /
/// `data = "reportCancel"` / `data.StartsWith "reportedUsed:"` branches).
/// Complements ReportFlowRealTests.fs, which only exercises the TEXT `/report <id>`
/// command, not this button chain.
///
/// Same single-account caveat as CouponLifecycleRealTests/ReportFlowRealTests: this
/// suite drives exactly ONE real Telegram account, so the coupon's owner and its
/// reporter are the same account throughout — that is expected and is exactly what
/// ReportFlowRealTests' text-`/report` test already does.
type ReportButtonFlowRealTests(fx: RealAssemblyFixture) =

    /// Adds a coupon (via the shared retry-safe helper) and immediately takes it, so
    /// every test in this file starts from "one taken coupon, held by this account" —
    /// the precondition for the `report` bottom-row button to appear on `/my`
    /// (CommandHandler.fs:257 only renders it when `taken` is non-empty).
    let addTakenCoupon (fixtureImage: string) (value: string) (minCheck: string) =
        task {
            let! couponId = RealTestHelpers.addCouponViaCaptionAsync fx fixtureImage value minCheck None
            let! takeSentId = fx.UserClient.SendText(fx.BotChatId, $"/take {couponId}")
            let! _taken = fx.UserClient.AwaitPhotoCaptionContaining(fx.BotChatId, takeSentId, "теперь твой", TimeSpan.FromSeconds 60.)
            return couponId
        }

    /// Sends `/my` and awaits the reply mentioning the given coupon id — the common
    /// entry point every test below uses to reach the `report` bottom-row button.
    let openMyMentioning (couponId: int) =
        task {
            let! mySentId = fx.UserClient.SendText(fx.BotChatId, "/my")
            return! fx.UserClient.AwaitTextContaining(fx.BotChatId, mySentId, $"Купон ID:{couponId}", TimeSpan.FromSeconds 60.)
        }

    /// Full happy-path chain: `/my` -> press `report` -> press `report:<id>` for the
    /// coupon set up by this test -> press `report:<id>:confirm`. Asserts the
    /// reporter-facing confirmation text (CommandHandler.fs's handleReport, "отправлен
    /// владельцу") and that the coupon's DB status becomes 'reported'
    /// (DbService.TryReportCoupon's terminal UPDATE).
    [<Fact>]
    member _.``report button chain: my -> report -> report id -> confirm marks the coupon reported``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let! couponId = addTakenCoupon "10_50_01-19_01-28_2706613152454.jpg" "10" "50"
            let! myMsg = openMyMentioning couponId

            do! fx.UserClient.PressCallbackButtonMatching(fx.BotChatId, myMsg, (fun d -> d = "report"), "report (bottom-row entry point)")
            let! selectMsg =
                fx.UserClient.AwaitTextContaining(fx.BotChatId, myMsg.id, "Какой купон уже использован?", TimeSpan.FromSeconds 60.)

            do!
                fx.UserClient.PressCallbackButtonMatching(
                    fx.BotChatId,
                    selectMsg,
                    (fun d -> d = $"report:{couponId}"),
                    $"report:{couponId}")
            let! confirmMsg =
                fx.UserClient.AwaitTextContaining(
                    fx.BotChatId,
                    selectMsg.id,
                    $"Купон ID:{couponId} уйдёт владельцу",
                    TimeSpan.FromSeconds 60.)
            Assert.Contains("Подтвердить?", confirmMsg.message)

            do!
                fx.UserClient.PressCallbackButtonMatching(
                    fx.BotChatId,
                    confirmMsg,
                    (fun d -> d = $"report:{couponId}:confirm"),
                    $"report:{couponId}:confirm")
            let! reporterReply =
                fx.UserClient.AwaitTextContaining(fx.BotChatId, confirmMsg.id, "отправлен владельцу", TimeSpan.FromSeconds 60.)
            Assert.Contains($"ID:{couponId}", reporterReply.message)

            let! status = DbSeed.getCouponStatusAsync fx.DbConnectionString couponId
            Assert.Equal("reported", status.status)
        })

    /// `reportCancel` pressed at the SELECT step (right after `/my` -> `report`, before
    /// picking a coupon). Asserts the "Ок, отменено." text and that the coupon's status
    /// is unchanged (still 'taken' — no report was ever filed).
    [<Fact>]
    member _.``reportCancel at the select step cancels without changing coupon status``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let! couponId = addTakenCoupon "10_50_01-21_01-30_2706616470579.jpg" "10" "50"
            let! myMsg = openMyMentioning couponId

            do! fx.UserClient.PressCallbackButtonMatching(fx.BotChatId, myMsg, (fun d -> d = "report"), "report (bottom-row entry point)")
            let! selectMsg =
                fx.UserClient.AwaitTextContaining(fx.BotChatId, myMsg.id, "Какой купон уже использован?", TimeSpan.FromSeconds 60.)

            do! fx.UserClient.PressCallbackButtonMatching(fx.BotChatId, selectMsg, (fun d -> d = "reportCancel"), "reportCancel")
            let! cancelReply =
                fx.UserClient.AwaitTextContaining(fx.BotChatId, selectMsg.id, "Ок, отменено.", TimeSpan.FromSeconds 60.)
            Assert.Equal("Ок, отменено.", cancelReply.message)

            let! status = DbSeed.getCouponStatusAsync fx.DbConnectionString couponId
            Assert.Equal("taken", status.status)

            // Cleanup, not assertion: the "still taken" assertion above is this test's own
            // point. Left 'taken' forever, this coupon would permanently eat one of this
            // account's shared MAX_TAKEN_COUPONS=6 slots for the rest of the serial run —
            // see DbSeed.releaseTakenCouponAsync's doc comment.
            do! DbSeed.releaseTakenCouponAsync fx.DbConnectionString couponId
        })

    /// `reportCancel` pressed at the CONFIRM step (`/my` -> `report` -> `report:<id>` ->
    /// `reportCancel`, one step further than the test above). Same assertions: "Ок,
    /// отменено." and an unchanged (still 'taken') coupon status.
    [<Fact>]
    member _.``reportCancel at the confirm step cancels without changing coupon status``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let! couponId = addTakenCoupon "5_25_01-15_01-21_2706528422291.jpg" "5" "25"
            let! myMsg = openMyMentioning couponId

            do! fx.UserClient.PressCallbackButtonMatching(fx.BotChatId, myMsg, (fun d -> d = "report"), "report (bottom-row entry point)")
            let! selectMsg =
                fx.UserClient.AwaitTextContaining(fx.BotChatId, myMsg.id, "Какой купон уже использован?", TimeSpan.FromSeconds 60.)

            do!
                fx.UserClient.PressCallbackButtonMatching(
                    fx.BotChatId,
                    selectMsg,
                    (fun d -> d = $"report:{couponId}"),
                    $"report:{couponId}")
            let! confirmMsg =
                fx.UserClient.AwaitTextContaining(
                    fx.BotChatId,
                    selectMsg.id,
                    $"Купон ID:{couponId} уйдёт владельцу",
                    TimeSpan.FromSeconds 60.)

            do! fx.UserClient.PressCallbackButtonMatching(fx.BotChatId, confirmMsg, (fun d -> d = "reportCancel"), "reportCancel")
            let! cancelReply =
                fx.UserClient.AwaitTextContaining(fx.BotChatId, confirmMsg.id, "Ок, отменено.", TimeSpan.FromSeconds 60.)
            Assert.Equal("Ок, отменено.", cancelReply.message)

            let! status = DbSeed.getCouponStatusAsync fx.DbConnectionString couponId
            Assert.Equal("taken", status.status)

            // Cleanup, not assertion — see the sibling test above's identical comment.
            do! DbSeed.releaseTakenCouponAsync fx.DbConnectionString couponId
        })

    /// Owner-side `reportedUsed:<id>` acknowledgement (§4). Setup drives the coupon into
    /// 'reported' via the TEXT `/report <id>` command rather than re-walking the button
    /// chain the first test above already covers — a more robust, shorter precondition
    /// for a test whose actual subject is the `/my` `reportedUsed:<id>` button, not the
    /// report chain itself.
    ///
    /// Also asserts what CommandHandler.fs's handleMy actually renders for a 'reported'
    /// coupon: it appears under the "⚠️ Отмечены как использованные вне бота" heading
    /// (CommandHandler.fs:238), and — unlike a 'taken' coupon's row — offers no
    /// "Вернуть"/`return:<id>` button, only `reportedUsed:<id>` (CommandHandler.fs:249-255:
    /// recirculating a known-dead coupon is the exact harm the report feature exists to
    /// stop).
    [<Fact>]
    member _.``reportedUsed button marks a reported coupon used from the owner's /my``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let! couponId = addTakenCoupon "5_25_01-15_01-21_2706666377231.jpg" "5" "25"

            let! reportSentId = fx.UserClient.SendText(fx.BotChatId, $"/report {couponId}")
            let! _reportReply =
                fx.UserClient.AwaitTextContaining(fx.BotChatId, reportSentId, "отправлен владельцу", TimeSpan.FromSeconds 60.)

            let! reportedStatus = DbSeed.getCouponStatusAsync fx.DbConnectionString couponId
            Assert.Equal("reported", reportedStatus.status)

            let! mySentId = fx.UserClient.SendText(fx.BotChatId, "/my")
            let! myMsg =
                fx.UserClient.AwaitTextContaining(
                    fx.BotChatId,
                    mySentId,
                    "Отмечены как использованные вне бота",
                    TimeSpan.FromSeconds 60.)
            Assert.Contains($"ID:{couponId}", myMsg.message)

            let hasReturn = fx.UserClient.FindCallbackData(myMsg, fun d -> d = $"return:{couponId}")
            Assert.True(hasReturn.IsNone, $"Did not expect a return:{couponId} button on a reported coupon's /my row")

            do!
                fx.UserClient.PressCallbackButtonMatching(
                    fx.BotChatId,
                    myMsg,
                    (fun d -> d = $"reportedUsed:{couponId}"),
                    $"reportedUsed:{couponId}")
            let! usedReply =
                fx.UserClient.AwaitTextContaining(fx.BotChatId, myMsg.id, $"Купон ID:{couponId} отмечен", TimeSpan.FromSeconds 60.)
            Assert.Contains("отмечен как использованный", usedReply.message)

            let! finalStatus = DbSeed.getCouponStatusAsync fx.DbConnectionString couponId
            Assert.Equal("used", finalStatus.status)
        })
