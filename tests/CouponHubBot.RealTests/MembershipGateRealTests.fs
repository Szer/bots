namespace CouponHubBot.RealTests

open System
open Xunit

/// Coverage item 9 (contract): "Membership gate: bot refuses a user who is not in the
/// CI group."
///
/// *** SELF-SKIPS UNLESS COUPON_REAL_TEST_RUN_MEMBERSHIP_GATE=1 — SEE BELOW. ***
///
/// TelegramMembershipService (MembershipService.fs:14-60) caches IsMember(userId) for
/// 1 day, keyed only by userId, with no way to force a fresh check short of the
/// service's own daily/startup invalidation, and no test-only bust hook exists (adding
/// one is a src/ change, out of this task's authorized scope — see the Deviations
/// section of this project's delivery report). This suite only ever drives ONE real
/// Telegram account (there is exactly one MTProto session — contract: "the session is
/// NOT reused"), so that SAME account is both "the member" every other real test in
/// this assembly depends on AND the only account available to prove the non-member
/// refusal path.
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
/// Why the opt-in guard: this test's own live check gets CACHED (false) for the rest
/// of the pod's lifetime, regardless of restoring the real COMMUNITY_CHAT_ID
/// afterward — restoring the DB row does not touch TelegramMembershipService's
/// in-memory dictionary. Whichever test runs it will poison every OTHER
/// membership-gated test that runs AFTERWARD in the SAME process. The coupon-real-test
/// pipeline's workflow invokes this whole project with one flat, unfiltered
/// `dotnet test tests/CouponHubBot.RealTests -c Release` (confirmed with the workflow
/// agent) — there is no per-run filtering or ordering guarantee to lean on, so this
/// test must default to OFF in that invocation or it silently breaks every other
/// coverage item that happens to run after it. Run it deliberately, alone, via
/// `make coupon-real-test-membership` (sets the opt-in env var and does NOT run the
/// rest of the suite in the same process) — against a pod you are prepared to
/// discard/recreate afterward, since the in-memory poisoning cannot be undone short of
/// a pod restart.
type MembershipGateRealTests(fx: RealAssemblyFixture) =

    [<Fact>]
    member _.``Non-member is refused (COMMUNITY_CHAT_ID pointed at a foreign chat)``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            if RealEnv.getVarOr "COUPON_REAL_TEST_RUN_MEMBERSHIP_GATE" "" <> "1" then
                Assert.Skip
                    "Membership-gate test poisons TelegramMembershipService's cache for the rest of this pod's \
                     life (see this class's doc comment) — set COUPON_REAL_TEST_RUN_MEMBERSHIP_GATE=1 and run it \
                     ALONE (e.g. `make coupon-real-test-membership`) against a pod you'll discard afterward."

            let env = fx.Env
            let! originalChatId = DbSeed.getSettingAsync fx.DbConnectionString "COMMUNITY_CHAT_ID"

            try
                // "1" is not a valid group/supergroup/channel id — getChatMember on it
                // always errors, which the app treats as "not a member".
                do! DbSeed.setSettingAsync fx.DbConnectionString "COMMUNITY_CHAT_ID" "1"

                use http = new Net.Http.HttpClient(Timeout = TimeSpan.FromSeconds 10.)
                http.DefaultRequestHeaders.Add("X-Telegram-Bot-Api-Secret-Token", env.BotAuthToken)
                let! reloadResp = http.PostAsync(env.ReloadSettingsUrl, null)
                Assert.True(reloadResp.IsSuccessStatusCode, $"POST {env.ReloadSettingsUrl} -> {int reloadResp.StatusCode}")

                let! sentId = fx.UserClient.SendText(fx.BotChatId, "/start")
                let! reply = fx.UserClient.AwaitTextContaining(fx.BotChatId, sentId, "только членам", TimeSpan.FromSeconds 60.)
                Assert.Contains("только членам", reply.message)
            finally
                // Always restore — a leftover bogus COMMUNITY_CHAT_ID would refuse every
                // other real test for the rest of this pod's life, on top of the
                // unavoidable in-memory cache poisoning described above.
                DbSeed.setSettingAsync fx.DbConnectionString "COMMUNITY_CHAT_ID" originalChatId
                |> fun t -> t.GetAwaiter().GetResult()

                use http = new Net.Http.HttpClient(Timeout = TimeSpan.FromSeconds 10.)
                http.DefaultRequestHeaders.Add("X-Telegram-Bot-Api-Secret-Token", env.BotAuthToken)
                (http.PostAsync(env.ReloadSettingsUrl, null)).GetAwaiter().GetResult() |> ignore
        })
