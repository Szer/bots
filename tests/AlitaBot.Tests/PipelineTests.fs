namespace AlitaBot.Tests

open System.Net
open System.Threading.Tasks
open BotTestInfra
open Xunit

[<Collection("AlitaBot")>]
type PipelineTests(fixture: DefaultAlitaTestContainers) =
    interface IClassFixture<DefaultAlitaTestContainers>

    [<Fact>]
    member _.``Mention triggers sendMessage via pipeline``() = task {
        do! fixture.ClearFakeCalls()
        do! fixture.SetFoundryCompletion("gpt-4o", "Привет!")

        let! resp = fixture.SendUpdate(Tg.groupMessage("@Alita привет", Tg.user(), fixture.TargetChatId))
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

        let! calls = fixture.GetFakeCalls("sendMessage")
        Assert.NotEmpty(calls)
    }

    [<Fact>]
    member _.``Non-mention message is ignored``() = task {
        do! fixture.ClearFakeCalls()

        let! resp = fixture.SendUpdate(Tg.groupMessage("Просто сообщение без упоминания", Tg.user(), fixture.TargetChatId))
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

        let! calls = fixture.GetFakeCalls("sendMessage")
        Assert.Empty(calls)
    }

    [<Fact>]
    member _.``Layer 3 silence → no sendMessage``() = task {
        do! fixture.SetLayer3Silence()
        do! fixture.ClearFakeCalls()

        let! resp = fixture.SendUpdate(Tg.groupMessage("@Alita скажи что-нибудь", Tg.user(), fixture.TargetChatId))
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

        let! calls = fixture.GetFakeCalls("sendMessage")
        Assert.Empty(calls)
        do! fixture.ResetLayer3Usual()
    }

    [<Fact>]
    member _.``Incoming message is written to message_log``() = task {
        let user = Tg.user()
        let! _ = fixture.SendUpdate(Tg.groupMessage("@Alita тест лога", user, fixture.TargetChatId))

        let! count =
            fixture.QuerySingle<int>(
                "SELECT COUNT(*)::INT FROM message_log WHERE user_id = @uid",
                {| uid = user.Id |})
        Assert.True(count > 0)
    }
