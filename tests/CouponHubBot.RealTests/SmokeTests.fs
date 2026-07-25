namespace CouponHubBot.RealTests

open System
open Xunit

/// Coverage item 1 (contract): "/start, /help reply."
///
/// Assumes the MTProto test account is a genuine member of the CI group at this
/// point — true for every test EXCEPT MembershipGateRealTests, which is why that one
/// must run in isolation rather than as part of this suite (see its doc comment).
type SmokeTests(fx: RealAssemblyFixture) =

    [<Fact>]
    member _.``start replies with a member welcome``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let! sentId = fx.UserClient.SendText(fx.BotChatId, "/start")
            let! reply = fx.UserClient.AwaitTextContaining(fx.BotChatId, sentId, "Привет", TimeSpan.FromSeconds 60.)
            Assert.Contains("/add", reply.message)
        })

    [<Fact>]
    member _.``help lists commands``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let! sentId = fx.UserClient.SendText(fx.BotChatId, "/help")
            let! reply = fx.UserClient.AwaitTextContaining(fx.BotChatId, sentId, "Команды", TimeSpan.FromSeconds 60.)
            Assert.Contains("/report", reply.message)
        })
