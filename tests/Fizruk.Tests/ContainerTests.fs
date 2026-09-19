module Fizruk.Tests.ContainerTests

open System.Net
open System.Net.Http
open System.Text.Json
open BotTestInfra
open Fizruk.Tests.ContainerFixture
open Fizruk.Tests.FakeCallHelpers
open Xunit

/// Pulls url/secret_token/allowed_updates out of a recorded setWebhook call body.
let private parseSetWebhookBody (body: string) =
    use doc = JsonDocument.Parse body
    let root = doc.RootElement
    let url = root.GetProperty("url").GetString()
    let secretToken =
        match root.TryGetProperty "secret_token" with
        | true, v -> Some(v.GetString())
        | false, _ -> None
    let allowedUpdates =
        match root.TryGetProperty "allowed_updates" with
        | true, v -> v.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> Array.ofSeq
        | false, _ -> [||]
    url, secretToken, allowedUpdates

/// Polls fakeTg's recorded calls for up to `timeoutMs` real milliseconds — the
/// webhook response returns before the bot's outbound sendMessage necessarily lands.
let rec private waitForCall
    (fixture: FizrukContainerFixture)
    (timeoutMs: int)
    (predicate: FakeCall array -> bool)
    : System.Threading.Tasks.Task<FakeCall array> =
    task {
        let sw = System.Diagnostics.Stopwatch.StartNew()
        let mutable calls = [||]
        let mutable ok = false
        while not ok && sw.ElapsedMilliseconds < int64 timeoutMs do
            let! got = fixture.GetFakeCalls "sendMessage"
            calls <- got
            ok <- predicate calls
            if not ok then do! System.Threading.Tasks.Task.Delay 100
        return calls
    }

type ContainerTests(fixture: FizrukContainerFixture) =

    [<Fact>]
    let ``container starts healthy and the health endpoint answers`` () =
        task {
            let! resp = fixture.BotHttp.GetAsync "/health"
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
            let! body = resp.Content.ReadAsStringAsync()
            Assert.Equal("OK", body)
        }

    [<Fact>]
    let ``status from an allowed chat produces a sendMessage with the expected text`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 1L, username = "player_one", firstName = "Player")
            let! resp = fixture.SendUpdate(Tg.groupMessage("/status", user, AllowedChatId))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = waitForCall fixture 5000 (fun calls -> anyCallHasText calls AllowedChatId "Demo Game")
            Assert.True(
                anyCallHasText calls AllowedChatId "Demo Game: stopped.",
                $"expected a 'Demo Game: stopped.' reply to chat {AllowedChatId}, got: %A{calls}")
        }

    [<Fact>]
    let ``a message from an unknown chat produces no sendMessage call`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 2L, username = "stranger", firstName = "Stranger")
            let! resp = fixture.SendUpdate(Tg.groupMessage("/status", user, UnknownChatId))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            // Negative assertion: give the (nonexistent) reply a real chance to land
            // before concluding it never will.
            do! System.Threading.Tasks.Task.Delay 1000
            let! calls = fixture.GetFakeCalls "sendMessage"
            Assert.Empty(calls |> Array.filter (fun c -> (parseCallBody c.Body |> Option.bind (fun p -> p.ChatId)) = Some UnknownChatId))
        }

    [<Fact>]
    let ``webhook self-registration calls setWebhook with the expected url, secret token, and allowed_updates`` () =
        let calls = fixture.WebhookRegistrationCalls
        Assert.Single(calls) |> ignore
        let url, secretToken, allowedUpdates = parseSetWebhookBody calls.[0].Body
        Assert.Equal(WebhookUrl, url)
        Assert.Equal(Some SecretToken, secretToken)
        Assert.Equal<string[]>([| "message" |], allowedUpdates)

    [<Fact>]
    let ``no setWebhook call is made by the container with BOT_WEBHOOK_URL unset`` () =
        // The one captured call (asserted above) belongs to webhookBotContainer —
        // botContainer never sets BOT_WEBHOOK_URL, so it contributes none.
        Assert.Single(fixture.WebhookRegistrationCalls) |> ignore
