namespace CouponHubBot.RealTests

open System
open Xunit

/// STUB — scaffolding only (contract: "Deliverable 2 — stub files"). No real coverage
/// yet; the placeholder [<Fact>] below exists only to keep this file compiling and
/// discoverable until an author replaces it.
///
/// WILL COVER: `/added` (list of coupons this account added), its `void:<id>` callback,
/// the `myAdded` callback (CallbackHandler.fs's `data = "myAdded"` branch), the TEXT
/// commands `/void`, `/used`, `/return`, and the bare `used:<id>` / `return:<id>`
/// buttons rendered under `/my` (distinct from the `:del`-suffixed take-confirmation-card
/// buttons CouponLifecycleRealTests.fs already covers — see that file's doc comment on
/// the `singleTakenKeyboard` vs `/my`-listing keyboard distinction). See
/// tests/CouponHubBot.RealTests/README.md for the full helper inventory — delete this
/// placeholder [<Fact>] once real coverage lands.
type MyAndAddedRealTests(fx: RealAssemblyFixture) =

    [<Fact>]
    member _.``placeholder: help round-trip``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let! sentId = fx.UserClient.SendText(fx.BotChatId, "/help")
            let! reply = fx.UserClient.AwaitTextContaining(fx.BotChatId, sentId, "Команды", TimeSpan.FromSeconds 60.)
            Assert.Contains("/my", reply.message)
        })
