namespace MultiPodTests

open System
open System.Threading.Tasks
open Dapper
open BotTestInfra
open Funogram.Telegram.Types
open MultiPodTests.FakeCallHelpers
open Xunit

type CouponMembershipTests(fixture: CouponMultiPodContainers) =

    [<Fact>]
    let ``Joins and leaves on one replica change access immediately on the other`` () = task {
        let user = Tg.user()
        let chat = Tg.chat(id = fixture.CommunityChatId)

        let checkAccess pod expected = task {
            do! fixture.ClearFakeCalls()
            use! reply = fixture.SendUpdateTo(pod, Tg.dmMessage("/start", user))
            Assert.True(reply.IsSuccessStatusCode)
            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls user.Id expected, $"Expected {expected} on replica {pod}")
        }

        let changeStatus updateId status oldMember newMember = task {
            do! fixture.SetChatMemberStatus(user.Id, status)
            let transition = ChatMemberUpdated.Create(chat, user, DateTime.UtcNow, oldMember, newMember)
            let update = Update.Create(updateId = updateId, chatMember = transition)
            use! reply = fixture.SendUpdateTo(0, update)
            Assert.True(reply.IsSuccessStatusCode)
        }

        let left = ChatMember.Left(ChatMemberLeft.Create("left", user))
        let joined = ChatMember.Member(ChatMemberMember.Create("member", user))
        do! fixture.SetChatMemberStatus(user.Id, "left")
        do! checkAccess 1 "только членам"
        do! changeStatus 960101L "member" left joined
        do! checkAccess 1 "Привет"
        do! checkAccess 0 "Привет"
        let! lookups = fixture.GetFakeCalls("getChatMember")
        Assert.Empty lookups
        do! changeStatus 960102L "left" joined left
        do! checkAccess 1 "только членам"
    }

    [<Fact>]
    let concurrentPersonalInviteRedemptions () = task {
        let sponsor = Tg.user(id = 969100L)
        use conn = new Npgsql.NpgsqlConnection(fixture.DbConnectionString)
        let key = {| sponsor = sponsor.Id |}
        let activeLink () = conn.QuerySingleAsync<string>(
            "SELECT link FROM community_invite WHERE sponsor=@sponsor AND applicant IS NULL", key)
        let send pod update = task {
            use! response = fixture.SendUpdateTo(pod, update)
            Assert.True(response.IsSuccessStatusCode)
        }
        do! fixture.ClearFakeCalls()
        let issue pod = send pod (Tg.dmMessage("/invite", sponsor))
        do! Task.WhenAll([| issue 0; issue 1 |]) :> Task
        let! first = activeLink()
        let! creates = fixture.GetFakeCalls("createChatInviteLink")
        let create = Assert.Single(creates)
        use parameters = System.Text.Json.JsonDocument.Parse(create.Body)
        Assert.True(parameters.RootElement.GetProperty("creates_join_request").GetBoolean())
        let! replies = fixture.GetFakeCalls("sendMessage")
        Assert.Equal(2, countCallsWithText replies sponsor.Id first)
        let chat = Tg.chat(id = fixture.CommunityChatId)
        let invite = ChatInviteLink.Create(first, Tg.user(), true, false, false)
        let request id =
            let user = Tg.user(id = id)
            let join = ChatJoinRequest.Create(chat, user, id, DateTime.UtcNow, inviteLink = invite)
            Update.Create(updateId = id, chatJoinRequest = join)
        let one, two = request 969101L, request 969102L
        do! Task.WhenAll([| send 0 one; send 1 two |]) :> Task
        do! send 1 one
        do! send 0 two
        let! approvals = fixture.GetFakeCalls("approveChatJoinRequest")
        Assert.Single(approvals) |> ignore
        let! winner = conn.QuerySingleAsync<int64>("SELECT applicant FROM community_invite WHERE link=@link", {| link = first |})
        let! invited = conn.QuerySingleAsync<int64>("""SELECT id FROM "user" WHERE invited_by=@sponsor""", key)
        Assert.Equal(winner, invited)
        let! second = activeLink()
        Assert.NotEqual<string>(first, second)
        do! issue 1
        let! allCreates = fixture.GetFakeCalls("createChatInviteLink")
        Assert.Equal(2, allCreates.Length)
        let legacy = Tg.user(id = 969104L)
        do! send 0 (Tg.dmMessage("/start", legacy))
        let secondInvite = { invite with InviteLink = second }
        let rejoin = ChatJoinRequest.Create(chat, legacy, legacy.Id, DateTime.UtcNow, inviteLink = secondInvite, queryId = "test-invite-query")
        do! send 1 (Update.Create(updateId = legacy.Id, chatJoinRequest = rejoin))
        let! isLegacy = conn.QuerySingleAsync<bool>("""SELECT invited_by IS NULL FROM "user" WHERE id=@id""", {| id = legacy.Id |})
        Assert.True(isLegacy)
        let! answers = fixture.GetFakeCalls("answerChatJoinRequestQuery")
        Assert.Single(answers) |> ignore
    }

    [<Fact>]
    let approvalRetryKeepsOriginalApplicant () = task {
        let sponsor = Tg.user(id = 969200L)
        use conn = new Npgsql.NpgsqlConnection(fixture.DbConnectionString)
        use! issue = fixture.SendUpdateTo(0, Tg.dmMessage("/invite", sponsor))
        Assert.True(issue.IsSuccessStatusCode)
        let! link = conn.QuerySingleAsync<string>("SELECT link FROM community_invite WHERE sponsor=@id", {| id = sponsor.Id |})
        let invite = ChatInviteLink.Create(link, Tg.user(), true, false, false)
        let request chatId userId invitation =
            let request = ChatJoinRequest.Create(Tg.chat(id = chatId), Tg.user(id = userId), userId, DateTime.UtcNow, inviteLink = invitation)
            Update.Create(updateId = userId, chatJoinRequest = request)
        let send pod chat uid invitation = fixture.SendUpdateTo(pod, request chat uid invitation)
        let chat = fixture.CommunityChatId
        let applicant = 969201L
        do! fixture.ClearFakeCalls()
        use! foreign = send 0 (chat - 1L) applicant invite
        use! unknown = send 1 chat applicant { invite with InviteLink = "unknown" }
        use! self = send 1 chat sponsor.Id invite
        let! untouched = fixture.GetFakeCalls("approveChatJoinRequest")
        Assert.Empty untouched
        do! fixture.SetMethodError("approveChatJoinRequest", true, errorCode = 503)
        let! failed = send 0 chat applicant invite
        do! fixture.SetMethodError("approveChatJoinRequest", false)
        use failedResponse = failed
        Assert.False(failedResponse.IsSuccessStatusCode)
        use! loser = send 1 chat (applicant + 1L) invite
        use! retry = send 1 chat applicant invite
        Assert.True(retry.IsSuccessStatusCode)
        let! invited = conn.QuerySingleAsync<int64>("""SELECT id FROM "user" WHERE invited_by=@id""", {| id = sponsor.Id |})
        Assert.Equal(applicant, invited)
        let! attempts = fixture.GetFakeCalls("approveChatJoinRequest")
        Assert.Equal(2, attempts.Length)
    }

    [<Fact>]
    let formerMemberCannotSponsorAnAdmission () = task {
        let sponsor = Tg.user(id = 969300L)
        use conn = new Npgsql.NpgsqlConnection(fixture.DbConnectionString)
        use! issue = fixture.SendUpdateTo(0, Tg.dmMessage("/invite", sponsor))
        Assert.True(issue.IsSuccessStatusCode)
        let! link = conn.QuerySingleAsync<string>("SELECT link FROM community_invite WHERE sponsor=@id", {| id = sponsor.Id |})
        do! fixture.SetChatMemberStatus(sponsor.Id, "left")
        do! fixture.ClearFakeCalls()
        let invite = ChatInviteLink.Create(link, Tg.user(), true, false, false)
        let applicant = Tg.user(id = 969301L)
        let request = ChatJoinRequest.Create(Tg.chat(id = fixture.CommunityChatId), applicant, applicant.Id, DateTime.UtcNow, inviteLink = invite)
        use! response = fixture.SendUpdateTo(1, Update.Create(updateId = applicant.Id, chatJoinRequest = request))
        Assert.True(response.IsSuccessStatusCode)
        let! approvals = fixture.GetFakeCalls("approveChatJoinRequest")
        Assert.Empty approvals
        let! revoked = fixture.GetFakeCalls("revokeChatInviteLink")
        Assert.Single(revoked) |> ignore
    }
