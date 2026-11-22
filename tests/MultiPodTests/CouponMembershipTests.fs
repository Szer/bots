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
            """SELECT invite_link FROM "user" WHERE id=@sponsor""", key)
        let send pod update = task {
            use! response = fixture.SendUpdateTo(pod, update)
            Assert.True(response.IsSuccessStatusCode)
        }
        do! fixture.ClearFakeCalls()
        let command = Tg.dmMessage("/invite", sponsor)
        do! Task.WhenAll([| send 0 command; send 1 (Tg.dmMessage("/invite", sponsor)) |]) :> Task
        let! first = activeLink()
        let! creates = fixture.GetFakeCalls("createChatInviteLink")
        let create = Assert.Single(creates)
        use parameters = System.Text.Json.JsonDocument.Parse(create.Body)
        Assert.Equal(fixture.CommunityChatId, parameters.RootElement.GetProperty("chat_id").GetInt64())
        Assert.Equal(1, parameters.RootElement.GetProperty("member_limit").GetInt32())
        Assert.False(parameters.RootElement.TryGetProperty("creates_join_request") |> fst)
        let! replies = fixture.GetFakeCalls("sendMessage")
        Assert.Equal(2, countCallsWithText replies sponsor.Id first)
        let legacy = Tg.user(id = 969104L)
        do! send 0 (Tg.dmMessage("/start", legacy))
        let joinedAt = DateTime.UtcNow
        let join link id =
            let invite = ChatInviteLink.Create(link, Tg.user(), false, false, false)
            let transition = ChatMemberUpdated.Create(Tg.chat(id = fixture.CommunityChatId), legacy, joinedAt,
                                ChatMember.Left(ChatMemberLeft.Create("left", legacy)),
                                ChatMember.Member(ChatMemberMember.Create("member", legacy)), inviteLink = invite)
            Update.Create(updateId = id, chatMember = transition)
        do! send 1 (join "unknown" 969101L)
        let used = join first 969102L
        do! fixture.SetMethodError("revokeChatInviteLink", true, errorCode = 503)
        use! failed = fixture.SendUpdateTo(0, used)
        do! fixture.SetMethodError("revokeChatInviteLink", false)
        Assert.False(failed.IsSuccessStatusCode)
        do! Task.WhenAll([| send 0 used; send 1 used |]) :> Task
        do! send 1 command
        let stream = $"coupon-invite:{fixture.CommunityChatId}:{sponsor.Id}"
        let! events = conn.QueryAsync<string>(
            """SELECT event_type FROM community_invite_event WHERE stream_id=@stream ORDER BY stream_version""", {| stream = stream |})
        Assert.Equal<string>([| "invite_link_created"; "invite_link_obtained"; "invite_link_obtained"; "invite_link_redeemed"; "invite_link_created" |], Seq.toArray events)
        let! actorsMatch = conn.QuerySingleAsync<bool>(
            """SELECT bool_and(CASE event_type
                   WHEN 'invite_link_obtained' THEN (data->>'actorId')::bigint=@sponsor
                   WHEN 'invite_link_redeemed' THEN (data->>'actorId')::bigint=@applicant
                   ELSE data->>'actorKind'='bot' END)
               FROM community_invite_event WHERE stream_id=@stream""",
            {| stream = stream; sponsor = sponsor.Id; applicant = legacy.Id |})
        Assert.True(actorsMatch)
        let! second = activeLink()
        Assert.NotEqual<string>(first, second)
        let! invitedBy = conn.QuerySingleAsync<int64>("""SELECT invited_by FROM "user" WHERE id=@id""", {| id = legacy.Id |})
        Assert.Equal(sponsor.Id, invitedBy)
        let! revoked = fixture.GetFakeCalls("revokeChatInviteLink")
        Assert.Equal(2, revoked.Length)
        let! allCreates = fixture.GetFakeCalls("createChatInviteLink")
        Assert.Equal(2, allCreates.Length)
        let laterSponsor = Tg.user(id = 969105L)
        do! send 1 (Tg.dmMessage("/invite", laterSponsor))
        let! laterLink = conn.QuerySingleAsync<string>("""SELECT invite_link FROM "user" WHERE id=@id""", {| id = laterSponsor.Id |})
        do! send 0 (join laterLink 969106L)
        do! send 1 (join second 969103L)
        let! current = conn.QuerySingleAsync<int64>("""SELECT invited_by FROM "user" WHERE id=@id""", {| id = legacy.Id |})
        Assert.Equal(laterSponsor.Id, current)
        let! admissions = conn.QuerySingleAsync<int>("SELECT count(*)::int FROM community_invite_event WHERE event_type='invite_link_redeemed' AND (data->>'actorId')::bigint=@id", {| id = legacy.Id |})
        Assert.Equal(3, admissions)
        let! savedLink = activeLink()
        do! send 0 (join first 969100L)
        let! afterLateJoin = activeLink()
        Assert.Equal<string>(savedLink, afterLateJoin)
        let! latestSponsor = conn.QuerySingleAsync<int64>("""SELECT invited_by FROM "user" WHERE id=@id""", {| id = legacy.Id |})
        Assert.Equal(laterSponsor.Id, latestSponsor)
        let! historicJoins = conn.QuerySingleAsync<int>(
            """SELECT count(*)::int FROM community_invite_event WHERE event_type='invite_link_redeemed'
               AND data->>'inviteId'=community_invite_key(@first)""", {| first = first |})
        Assert.Equal(2, historicJoins)
    }
