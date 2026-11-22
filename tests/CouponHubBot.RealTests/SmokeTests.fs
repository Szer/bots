namespace CouponHubBot.RealTests

open System
open System.Threading.Tasks
open Dapper
open Npgsql
open Xunit

/// Coverage item 1 (contract): "/start, /help reply."
///
/// Assumes the MTProto test account is a genuine member of the CI group at this
/// point — true for every test EXCEPT MembershipGateRealTests, which is why that one
/// must run in isolation rather than as part of this suite (see its doc comment).
type SmokeTests(fx: RealAssemblyFixture) =

    let waitForJoin link (sponsor: int64) (applicant: int64) = task {
        use conn = new NpgsqlConnection(fx.DbConnectionString)
        let key = {| chat = fx.Env.TestChatId; sponsor = sponsor; applicant = applicant; link = link |}
        let proof = """
            SELECT EXISTS (
                SELECT 1 FROM community_invite_event e JOIN "user" u ON u.id=@applicant
                WHERE e.event_type='invite_link_redeemed' AND e.data->>'inviteId'=community_invite_key(@link)
                  AND (e.data->>'chatId')::bigint=@chat AND (e.data->>'actorId')::bigint=@applicant
                  AND (e.data->>'sponsorId')::bigint=@sponsor AND u.invited_by=@sponsor
                  AND NOT EXISTS (SELECT 1 FROM "user" WHERE id=@sponsor AND invite_link=@link)
            )"""
        let deadline = DateTime.UtcNow.AddMinutes(10.)
        let mutable matched = false
        while not matched && DateTime.UtcNow < deadline do
            let! found = conn.QuerySingleAsync<bool>(proof, key)
            matched <- found
            if not matched then do! Task.Delay(TimeSpan.FromSeconds 2.)
        Assert.True(matched, "No matching join receipt and sponsor attribution within ten minutes.")
        printfn "Verified the membership update matched the issued link, the expected participant, and the expected sponsor."
    }

    let verifyPersonalJoin link = task {
        let sponsor = fx.UserClient.Me.id
        let username = Environment.GetEnvironmentVariable("JOINER_USERNAME")
        let! applicant = fx.UserClient.ResolveUserByUsername(username.Trim().TrimStart('@'))
        Assert.True((applicant <> sponsor), "The participant and the MTProto sponsor must use different accounts.")
        let! _ = fx.UserClient.SendText(applicant, $"Проверка приглашения в тестовую группу. Открой ссылку и нажми «Вступить» в течение 10 минут.\n{link}")
        do! waitForJoin link sponsor applicant
    }

    let verifyOwnerInvite () = task {
        let username = Environment.GetEnvironmentVariable("JOINER_USERNAME")
        let! sponsor = fx.UserClient.ResolveUserByUsername(username.Trim().TrimStart('@'))
        let applicant = fx.UserClient.Me.id
        Assert.True((sponsor <> applicant), "The sponsor and participant must use different accounts.")
        use conn = new NpgsqlConnection(fx.DbConnectionString)
        let deadline = DateTime.UtcNow.AddMinutes(10.)
        let mutable link = ""
        while String.IsNullOrEmpty link && DateTime.UtcNow < deadline do
            let! issued = conn.QuerySingleOrDefaultAsync<string>(
                """SELECT invite_link FROM "user" WHERE invite_chat_id=@chat AND id=@sponsor""",
                {| chat = fx.Env.TestChatId; sponsor = sponsor |})
            link <- issued
            if String.IsNullOrEmpty link then do! Task.Delay(TimeSpan.FromSeconds 2.)
        Assert.False(String.IsNullOrEmpty link, "The owner did not request an invitation within ten minutes.")
        do! fx.UserClient.RejoinTestGroup(fx.Env.TestChatId, link)
        do! waitForJoin link sponsor applicant
    }

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
                    if Environment.GetEnvironmentVariable("VERIFY_OWNER_INVITE") = "true" then
                        do! verifyOwnerInvite ()
                    else
                        let! first = issue()
                        let! second = issue()
                        Assert.True(first.StartsWith("https://t.me/+"))
                        Assert.True((first = second))
                        if Environment.GetEnvironmentVariable("VERIFY_PERSONAL_JOIN") = "true" then
                            do! verifyPersonalJoin first
                    return Ok ()
                with ex -> return Error ex
            }
            do! DbSeed.setSettingAsync fx.DbConnectionString setting original
            do! fx.ReloadSettingsAsync()
            match outcome with
            | Ok () -> ()
            | Error ex -> System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw ex
        })
