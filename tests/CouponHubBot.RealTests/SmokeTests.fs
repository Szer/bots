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

    [<Fact>]
    member _.PersonalInviteIsStableUntilRedemption() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()
            let setting = "MANAGED_INVITES_ENABLED"
            let! original = DbSeed.getSettingAsync fx.DbConnectionString setting
            let issue () = task {
                let! sent = fx.UserClient.SendText(fx.BotChatId, "/invite")
                let! reply = fx.UserClient.AwaitTextContaining(fx.BotChatId, sent, "По ней можно вступить один раз", TimeSpan.FromSeconds 60.)
                return reply.message.Split('\n') |> Array.last
            }
            let! outcome = task {
                try
                    do! DbSeed.setSettingAsync fx.DbConnectionString setting "true"
                    do! fx.ReloadSettingsAsync()
                    let! first = issue()
                    let! second = issue()
                    Assert.True(first.StartsWith("https://t.me/+"))
                    Assert.True((first = second))
                    return Ok ()
                with ex -> return Error ex
            }
            do! DbSeed.setSettingAsync fx.DbConnectionString setting original
            do! fx.ReloadSettingsAsync()
            match outcome with
            | Ok () -> ()
            | Error ex -> System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw ex
        })
