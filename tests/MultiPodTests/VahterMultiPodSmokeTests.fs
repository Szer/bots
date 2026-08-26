namespace MultiPodTests

open System.Net
open System.Text.Json
open BotTestInfra
open MultiPodTests.FakeCallHelpers
open Xunit

/// Proves the multi-pod harness itself (BotTestInfra.MultiPodContainerBase) works for
/// VahterBanBot. Feature-specific multi-pod behavior (settings propagation, reminder lease,
/// debounce, spam-text) is added by later PRs stacking on this one — deliberately not here.
type VahterMultiPodSmokeTests(fixture: VahterMultiPodContainers) =

    [<Fact>]
    let ``Both instances become ready`` () = task {
        for i in 0 .. fixture.InstanceCount - 1 do
            let! resp = fixture.BotHttp(i).GetAsync("/ready")
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
    }

    [<Fact>]
    let ``Webhook update to each instance produces FakeTgApi traffic`` () = task {
        do! fixture.ClearFakeCalls()
        let! resp0 = fixture.SendUpdateTo(0, Tg.quickMsg("/ban ping", chat = fixture.ChatsToMonitor, from = fixture.Vahter))
        Assert.Equal(HttpStatusCode.OK, resp0.StatusCode)
        let! calls0 = fixture.GetFakeCalls("sendMessage")
        Assert.True(findCallWithText calls0 fixture.ChatsToMonitor.Id "pong",
            "Expected instance 0's /ban ping to produce a pong sendMessage call")

        do! fixture.ClearFakeCalls()
        let! resp1 = fixture.SendUpdateTo(1, Tg.quickMsg("/ban ping", chat = fixture.ChatsToMonitor, from = fixture.Vahter))
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode)
        let! calls1 = fixture.GetFakeCalls("sendMessage")
        Assert.True(findCallWithText calls1 fixture.ChatsToMonitor.Id "pong",
            "Expected instance 1's /ban ping to produce a pong sendMessage call")
    }

    [<Fact>]
    let ``Settings dump is 200, parses, and never leaks the secret token on either instance`` () = task {
        for i in 0 .. fixture.InstanceCount - 1 do
            let! json = fixture.GetSettingsDump(i)
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement
            Assert.Equal(1337L, root.GetProperty("BotUserId").GetInt64())
            Assert.True(root.GetProperty("BotToken").GetProperty("present").GetBoolean())
            Assert.DoesNotContain("123:456", json)
    }
