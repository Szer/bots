module Fizruk.Tests.ContainerFixture

open System
open System.IO
open System.Net.Http
open System.Net.Http.Json
open System.Text
open System.Threading.Tasks
open DotNet.Testcontainers.Builders
open DotNet.Testcontainers.Configurations
open DotNet.Testcontainers.Containers
open BotTestInfra
open BotTestInfra.ContainerHelpers
open Xunit

/// Mounted into the bot container as FIZRUK_CONFIG_PATH: one game with no player
/// probe, one chat id allowed to control it (matches AllowedChatId below).
let ConfigJson =
    """
    {
      "games": {
        "demo-game": {
          "displayName": "Demo Game",
          "namespace": "games",
          "deployment": "demo-game",
          "podSelector": "app=demo-game",
          "container": "server",
          "nodeLabelSelector": "workload=game",
          "address": "demo.szer.dev:1234",
          "activityRegex": "\\[(JOIN|LEAVE)\\]",
          "idleGraceMinutes": 45,
          "idleWindowMinutes": 60,
          "startTimeoutMinutes": 20
        }
      },
      "chats": { "100": ["demo-game"] }
    }
    """

let AllowedChatId = 100L

let UnknownChatId = 999L

let SecretToken = "fizruk-test-secret"

/// Sentinel BOT_WEBHOOK_URL for the second (webhook-registration-enabled) bot
/// container — never dialed, FakeTgApi only records the setWebhook call it causes.
let WebhookUrl = "http://fizruk.invalid/bot"

/// Runs the real Fizruk image (src/Dockerfile.bot, BOT_PROJECT=Fizruk) against a
/// FakeTgApi container standing in for api.telegram.org, with FIZRUK_FAKE_K8S=true.
type FizrukContainerFixture() =
    let solutionDir = CommonDirectoryPath.GetSolutionDirectory()
    let solutionDirPath = solutionDir.DirectoryPath
    let network = createNetwork ()
    let fakeAlias = "fake-tg-api"

    let fakeTgImage, fakeTgBuildLogger =
        getOrCreateImageSpec "fizruk-tests-fake-tg-api" (fun () ->
            buildImageSpec solutionDir "./tests/Dockerfile.fake" "fizruk-tests-fake-tg-api" true true
                [ "FAKE_PROJECT", "FakeTgApi"; "FAKE_PORT", "8080" ])
    let fakeTgContainer = createFakeTgApiContainer fakeTgImage network fakeAlias

    let botImage, botBuildLogger =
        getOrCreateImageSpec "fizruk-tests-bot" (fun () ->
            buildImageSpec solutionDir "./src/Dockerfile.bot" "fizruk-tests-bot" true true [ "BOT_PROJECT", "Fizruk" ])

    // A host-file bind mount, not WithResourceMapping — the latter's docker-cp
    // tar-stream copy 500s ("broken pipe") against this podman version.
    let configFilePath = Path.Combine(Path.GetTempPath(), $"fizruk-test-config-{Guid.NewGuid()}.json")
    do File.WriteAllText(configFilePath, ConfigJson)

    let botContainer =
        ContainerBuilder(botImage)
            .WithNetwork(network)
            .WithPortBinding(80, true)
            .WithBindMount(configFilePath, "/config/fizruk.json", AccessMode.ReadOnly)
            // Without label=disable, SELinux-enforcing hosts block reading the bind
            // mount — a no-op where SELinux isn't in use (see Flyway's bind mount).
            .WithCreateParameterModifier(fun p -> p.HostConfig.SecurityOpt <- ResizeArray [ "label=disable" ])
            .WithEnvironment("ASPNETCORE_HTTP_PORTS", "80")
            .WithEnvironment("FIZRUK_CONFIG_PATH", "/config/fizruk.json")
            .WithEnvironment("BOT_TELEGRAM_TOKEN", "test-token")
            .WithEnvironment("BOT_AUTH_TOKEN", SecretToken)
            .WithEnvironment("TELEGRAM_API_URL", $"http://{fakeAlias}:8080")
            .WithEnvironment("FIZRUK_FAKE_K8S", "true")
            .DependsOn(fakeTgContainer)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(80))
            .Build()

    /// Same image/config, BOT_WEBHOOK_URL set — exercises webhook self-registration
    /// without a second image build (Testcontainers caches botImage by name).
    let webhookBotContainer =
        ContainerBuilder(botImage)
            .WithNetwork(network)
            .WithPortBinding(80, true)
            .WithBindMount(configFilePath, "/config/fizruk.json", AccessMode.ReadOnly)
            .WithCreateParameterModifier(fun p -> p.HostConfig.SecurityOpt <- ResizeArray [ "label=disable" ])
            .WithEnvironment("ASPNETCORE_HTTP_PORTS", "80")
            .WithEnvironment("FIZRUK_CONFIG_PATH", "/config/fizruk.json")
            .WithEnvironment("BOT_TELEGRAM_TOKEN", "test-token")
            .WithEnvironment("BOT_AUTH_TOKEN", SecretToken)
            .WithEnvironment("TELEGRAM_API_URL", $"http://{fakeAlias}:8080")
            .WithEnvironment("FIZRUK_FAKE_K8S", "true")
            .WithEnvironment("BOT_WEBHOOK_URL", WebhookUrl)
            .DependsOn(fakeTgContainer)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(80))
            .Build()

    let mutable botHttp: HttpClient = null
    let mutable fakeTgHttp: HttpClient = null
    let mutable testArtifactsDir: string = null
    let mutable webhookRegistrationCalls: FakeCall array = [||]

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            ValueTask(task {
                testArtifactsDir <- Path.Combine(solutionDirPath, "test-artifacts", "Fizruk.Tests", "ContainerTests")

                let botBuildTask = buildImageOncePerProcess "fizruk-tests-bot" testArtifactsDir "bot" botImage botBuildLogger
                let fakeTgBuildTask =
                    buildImageOncePerProcess "fizruk-tests-fake-tg-api" testArtifactsDir "fake-tg-api" fakeTgImage fakeTgBuildLogger
                do! Task.WhenAll(botBuildTask, fakeTgBuildTask)

                do! fakeTgContainer.StartAsync()
                do! Task.WhenAll(botContainer.StartAsync(), webhookBotContainer.StartAsync())

                botHttp <- new HttpClient(BaseAddress = Uri($"http://127.0.0.1:{botContainer.GetMappedPublicPort(80)}"))
                botHttp.Timeout <- TimeSpan.FromSeconds 15.0
                botHttp.DefaultRequestHeaders.Add("X-Telegram-Bot-Api-Secret-Token", SecretToken)

                fakeTgHttp <- new HttpClient(BaseAddress = Uri($"http://127.0.0.1:{fakeTgContainer.GetMappedPublicPort(8080)}"))
                fakeTgHttp.Timeout <- TimeSpan.FromSeconds 5.0

                // Snapshot the (fire-and-forget, so not necessarily instant) setWebhook
                // call now, before any test's ClearFakeCalls() can wipe the evidence.
                let sw = Diagnostics.Stopwatch.StartNew()
                let mutable snapshot: FakeCall array = [||]
                while snapshot.Length = 0 && sw.ElapsedMilliseconds < 10_000L do
                    let! calls = fakeTgHttp.GetFromJsonAsync<FakeCall array>("/test/calls?method=setWebhook")
                    snapshot <- calls
                    if snapshot.Length = 0 then do! Task.Delay 100
                webhookRegistrationCalls <- snapshot
            } :> Task)

        member _.DisposeAsync() =
            ValueTask(task {
                let! _ = dumpContainerLogs testArtifactsDir "bot" botContainer
                let! _ = dumpContainerLogs testArtifactsDir "webhook-bot" webhookBotContainer
                let! _ = dumpContainerLogs testArtifactsDir "fake-tg-api" fakeTgContainer
                if not (isNull botHttp) then botHttp.Dispose()
                if not (isNull fakeTgHttp) then fakeTgHttp.Dispose()
                do! botContainer.DisposeAsync()
                do! webhookBotContainer.DisposeAsync()
                do! fakeTgContainer.DisposeAsync()
                File.Delete configFilePath
            } :> Task)

    member _.BotHttp = botHttp

    /// setWebhook calls snapshotted once at startup, independent of any test's
    /// ClearFakeCalls() — expected to be exactly one, from webhookBotContainer.
    member _.WebhookRegistrationCalls = webhookRegistrationCalls

    member _.SendUpdate(update: Funogram.Telegram.Types.Update) =
        task {
            let json = Encoding.UTF8.GetString(Funogram.Tools.toJson update)
            use content = new StringContent(json, Encoding.UTF8, "application/json")
            return! botHttp.PostAsync("/bot", content)
        }

    member _.ClearFakeCalls() =
        task {
            let! _ = fakeTgHttp.DeleteAsync "/test/calls"
            return ()
        }

    member _.GetFakeCalls(methodName: string) =
        task { return! fakeTgHttp.GetFromJsonAsync<FakeCall array>($"/test/calls?method={methodName}") }
