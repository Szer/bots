namespace CouponHubBot.RealTests

open System
open Xunit

/// Checks a fresh membership lookup against a foreign chat using the real Telegram account.
type MembershipGateRealTests(fx: RealAssemblyFixture) =

    [<Fact>]
    member _.``Non-member is refused (COMMUNITY_CHAT_ID pointed at a foreign chat)``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let! originalChatId = DbSeed.getSettingAsync fx.DbConnectionString "COMMUNITY_CHAT_ID"

            try
                // "1" is not a valid group/supergroup/channel id — getChatMember on it
                // always errors, which the app treats as "not a member".
                do! DbSeed.setSettingAsync fx.DbConnectionString "COMMUNITY_CHAT_ID" "1"
                do! fx.ReloadSettingsAsync()
                do! fx.InvalidateMembershipCacheAsync()

                let! sentId = fx.UserClient.SendText(fx.BotChatId, "/start")
                let! reply = fx.UserClient.AwaitTextContaining(fx.BotChatId, sentId, "только членам", TimeSpan.FromSeconds 60.)
                Assert.Contains("только членам", reply.message)
            finally
                DbSeed.setSettingAsync fx.DbConnectionString "COMMUNITY_CHAT_ID" originalChatId
                |> fun t -> t.GetAwaiter().GetResult()

                fx.ReloadSettingsAsync().GetAwaiter().GetResult()
                fx.InvalidateMembershipCacheAsync().GetAwaiter().GetResult()
        })
