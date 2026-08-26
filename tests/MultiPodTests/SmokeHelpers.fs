/// Shared assertions for the boot-only smoke suites (VahterMultiPodSmokeTests,
/// CouponMultiPodSmokeTests) — generic over any BotContainerBase-derived multi-pod fixture, so
/// per-bot files only keep the thin facts plus their bot-specific webhook/clock scenarios.
module MultiPodTests.SmokeHelpers

open System.Net
open System.Text.Json
open BotTestInfra
open Xunit

/// Asserts every instance's /ready returns 200.
let assertAllInstancesReady (fixture: BotContainerBase) = task {
    for i in 0 .. fixture.InstanceCount - 1 do
        let! resp = fixture.BotHttpAt(i).GetAsync("/ready")
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
}

/// Asserts /config-dump is 200 (auth accepted), parses, exposes a known non-secret setting
/// (`assertKnownSetting`), redacts BotToken to `{present:true}`, and never leaks
/// `secretLiteral` (the fake token value seeded into AppEnvVars) anywhere in the raw JSON —
/// on every instance.
let assertSettingsDumpAuthorizedAndRedacted
    (fixture: BotContainerBase)
    (secretLiteral: string)
    (assertKnownSetting: JsonElement -> unit)
    =
    task {
        for i in 0 .. fixture.InstanceCount - 1 do
            let! json = fixture.GetSettingsDump(i)
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement
            assertKnownSetting root
            Assert.True(root.GetProperty("BotToken").GetProperty("present").GetBoolean())
            Assert.DoesNotContain(secretLiteral, json)
    }
