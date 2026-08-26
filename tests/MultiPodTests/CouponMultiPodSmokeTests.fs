namespace MultiPodTests

open System.Net
open System.Text.Json
open BotTestInfra
open MultiPodTests.FakeCallHelpers
open Xunit

/// Proves the multi-pod harness itself (BotTestInfra.MultiPodContainerBase) works for
/// CouponHubBot, including the TEST_MODE lockstep clock helper. Feature-specific multi-pod
/// behavior (reminder lease, debounce) is added by later PRs stacking on this one.
type CouponMultiPodSmokeTests(fixture: CouponMultiPodContainers) =

    [<Fact>]
    let ``Both instances become ready`` () = task {
        for i in 0 .. fixture.InstanceCount - 1 do
            let! resp = fixture.BotHttp(i).GetAsync("/ready")
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
    }

    [<Fact>]
    let ``Webhook update to each instance produces FakeTgApi traffic`` () = task {
        do! fixture.ClearFakeCalls()
        let user0 = Tg.user(id = 950L, username = "multipod_member_0")
        do! fixture.SetChatMemberStatus(user0.Id, "member")
        let! resp0 = fixture.SendUpdateTo(0, Tg.dmMessage("/start", user0))
        Assert.Equal(HttpStatusCode.OK, resp0.StatusCode)
        let! calls0 = fixture.GetFakeCalls("sendMessage")
        Assert.True(findCallWithText calls0 user0.Id "Привет", "Expected instance 0's /start to greet the member")

        do! fixture.ClearFakeCalls()
        let user1 = Tg.user(id = 951L, username = "multipod_member_1")
        do! fixture.SetChatMemberStatus(user1.Id, "member")
        let! resp1 = fixture.SendUpdateTo(1, Tg.dmMessage("/start", user1))
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode)
        let! calls1 = fixture.GetFakeCalls("sendMessage")
        Assert.True(findCallWithText calls1 user1.Id "Привет", "Expected instance 1's /start to greet the member")
    }

    [<Fact>]
    let ``Settings dump is 200, parses, and never leaks the secret token on either instance`` () = task {
        for i in 0 .. fixture.InstanceCount - 1 do
            let! json = fixture.GetSettingsDump(i)
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement
            Assert.Equal(fixture.CommunityChatId, root.GetProperty("CommunityChatId").GetInt64())
            Assert.True(root.GetProperty("BotToken").GetProperty("present").GetBoolean())
            Assert.DoesNotContain("123:456", json)
    }

    [<Fact>]
    let ``AdvanceAllClocks round-trips on both instances`` () = task {
        // AdvanceAllClocks throws (EnsureSuccessStatusCode) if either instance rejects the
        // advance — reaching the assertions below proves both accepted it in lockstep.
        do! fixture.AdvanceAllClocks(5000)
        for i in 0 .. fixture.InstanceCount - 1 do
            let! resp = fixture.BotHttp(i).GetAsync("/health")
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
    }
