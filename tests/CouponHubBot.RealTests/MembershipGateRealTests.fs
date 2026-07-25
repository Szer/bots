namespace CouponHubBot.RealTests

open System
open Xunit

/// Coverage item 9 (contract): "Membership gate: bot refuses a user who is not in the
/// CI group."
///
/// TelegramMembershipService (MembershipService.fs:14-60) caches IsMember(userId) for
/// 1 day, keyed only by userId. This suite only ever drives ONE real Telegram account
/// (there is exactly one MTProto session — contract: "the session is NOT reused"), so
/// that SAME account is both "the member" every other real test in this assembly
/// depends on AND the only account available to prove the non-member refusal path.
///
/// Mechanism: flips the bot's *configured* COMMUNITY_CHAT_ID (a DB-only bot_setting
/// key, reloadable via POST /reload-settings with no pod restart) to a chat id this
/// account provably is NOT a member of, so the same live getChatMember code path runs
/// against a definitely-foreign chat (Telegram's getChatMember on an unknown/
/// inaccessible chat errors, which TelegramMembershipService.IsMember explicitly
/// treats as "not a member" — see its `with ex -> ... return false`). Flipping the
/// account's REAL Telegram membership instead (banChatMember) would need a bot token,
/// which this project deliberately never reads (see RealEnv.fs's doc comment).
///
/// Runs in the DEFAULT unfiltered suite now (no opt-in guard, no isolation needed):
/// POST /test/membership/invalidate (Program.fs, TelegramMembershipService.InvalidateCache)
/// makes the flip/restore fully observable and fully reversible within a single pod, so
/// this no longer poisons any test that runs after it. Sequence, and why the order matters:
///   1. flip COMMUNITY_CHAT_ID to a foreign chat + /reload-settings (config now wrong)
///   2. invalidate (clear any stale cached verdict — otherwise a cached `true` from an
///      earlier test in this same pod would mask step 3 entirely, exactly the flakiness
///      this hook exists to prevent)
///   3. send /start, assert the refusal text — this is now a genuinely fresh,
///      genuinely negative getChatMember call, not a cache read
///   4. `finally`: restore COMMUNITY_CHAT_ID -> /reload-settings (config correct again)
///      -> invalidate AGAIN, so this test's own negative verdict (cached during step 3)
///      cannot leak into any OTHER test that runs later in the same pod
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
                // Always restore, and ALWAYS invalidate again afterward — a leftover
                // bogus COMMUNITY_CHAT_ID (or just this test's own cached `false` for
                // this account) would refuse every other real test for the rest of
                // this pod's life otherwise.
                DbSeed.setSettingAsync fx.DbConnectionString "COMMUNITY_CHAT_ID" originalChatId
                |> fun t -> t.GetAwaiter().GetResult()

                fx.ReloadSettingsAsync().GetAwaiter().GetResult()
                fx.InvalidateMembershipCacheAsync().GetAwaiter().GetResult()
        })
