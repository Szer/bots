namespace CouponHubBot.RealTests

open System
open Xunit

/// STUB — scaffolding only (contract: "Deliverable 2 — stub files"). No real coverage
/// yet; the placeholder [<Fact>] below exists only to keep this file compiling and
/// discoverable until an author replaces it.
///
/// WILL COVER: the interactive `/add` wizard — bare `/add` -> photo -> `addflow:ocr:yes`
/// / `addflow:ocr:no` -> `addflow:disc:<value>:<min_check>` -> `addflow:date:today` /
/// `addflow:date:tomorrow` / custom-date text entry -> `addflow:confirm` /
/// `addflow:cancel` (CallbackHandler.fs:203-346, CouponFlowHandler.fs), plus the
/// separate `addflow:bulk:cancel` album-flow cancel button. See
/// tests/CouponHubBot.RealTests/README.md for the full helper inventory
/// (RealTestHelpers.addCouponViaCaptionAsync/fixtureBarcodes, DbSeed.deletePendingAddFlowAsync
/// / deletePendingBatchesAsync, TgUserClient.AwaitAndPressButton /
/// PressCallbackButtonMatching) — delete this placeholder [<Fact>] once real coverage lands.
type AddWizardRealTests(fx: RealAssemblyFixture) =

    [<Fact>]
    member _.``placeholder: help round-trip``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let! sentId = fx.UserClient.SendText(fx.BotChatId, "/help")
            let! reply = fx.UserClient.AwaitTextContaining(fx.BotChatId, sentId, "Команды", TimeSpan.FromSeconds 60.)
            Assert.Contains("/add", reply.message)
        })
