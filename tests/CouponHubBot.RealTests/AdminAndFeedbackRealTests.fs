namespace CouponHubBot.RealTests

open System
open Xunit

/// STUB — scaffolding only (contract: "Deliverable 2 — stub files"). No real coverage
/// yet; the placeholder [<Fact>] below exists only to keep this file compiling and
/// discoverable until an author replaces it.
///
/// WILL COVER: the `/feedback` flow, admin-only `/debug` and `/undo`, and the bot's
/// short command aliases (see CommandHandler.fs's Dispatch for the alias table). The
/// admin commands need FEEDBACK_ADMINS to include the logged-in MTProto test user's own
/// id — already true by construction, since RealAssemblyFixture seeds FEEDBACK_ADMINS
/// with exactly that id at startup (RealAssemblyFixture.fs's InitializeAsync /
/// DbSeed.applyAsync). See tests/CouponHubBot.RealTests/README.md for the full helper
/// inventory — delete this placeholder [<Fact>] once real coverage lands.
type AdminAndFeedbackRealTests(fx: RealAssemblyFixture) =

    [<Fact>]
    member _.``placeholder: help round-trip``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let! sentId = fx.UserClient.SendText(fx.BotChatId, "/help")
            let! reply = fx.UserClient.AwaitTextContaining(fx.BotChatId, sentId, "Команды", TimeSpan.FromSeconds 60.)
            Assert.Contains("/feedback", reply.message)
        })
