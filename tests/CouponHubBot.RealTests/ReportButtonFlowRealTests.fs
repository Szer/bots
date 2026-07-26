namespace CouponHubBot.RealTests

open System
open Xunit

/// STUB — scaffolding only (contract: "Deliverable 2 — stub files"). No real coverage
/// yet; the placeholder [<Fact>] below exists only to keep this file compiling and
/// discoverable until an author replaces it.
///
/// WILL COVER: the button-driven `/report` flow — `/my` -> `report` -> `report:<id>`
/// (coupon selection) -> `report:<id>:confirm`, `reportCancel`, and the adder-side
/// `reportedUsed:<id>` acknowledgement (CallbackHandler.fs's `data = "report"` /
/// `data.StartsWith "report:"` / `data = "reportCancel"` /
/// `data.StartsWith "reportedUsed:"` branches). Complements ReportFlowRealTests.fs,
/// which only exercises the TEXT `/report <id>` command, not this button chain. See
/// tests/CouponHubBot.RealTests/README.md for the full helper inventory — delete this
/// placeholder [<Fact>] once real coverage lands.
type ReportButtonFlowRealTests(fx: RealAssemblyFixture) =

    [<Fact>]
    member _.``placeholder: help round-trip``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let! sentId = fx.UserClient.SendText(fx.BotChatId, "/help")
            let! reply = fx.UserClient.AwaitTextContaining(fx.BotChatId, sentId, "Команды", TimeSpan.FromSeconds 60.)
            Assert.Contains("/report", reply.message)
        })
