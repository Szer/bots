namespace MultiPodTests

open System
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
