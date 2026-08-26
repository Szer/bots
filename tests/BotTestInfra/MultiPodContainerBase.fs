namespace BotTestInfra

open System
open System.IO
open System.Net.Http
open System.Net.Http.Json
open System.Text
open System.Threading.Tasks
open DotNet.Testcontainers.Builders
open DotNet.Testcontainers.Configurations
open DotNet.Testcontainers.Containers
open DotNet.Testcontainers.Images
open Testcontainers.PostgreSql
open BotTestInfra.ContainerHelpers
open Xunit

/// Config for an N-instance fixture. Wraps BotContainerConfig unchanged (same fields drive the
/// shared network/db/fake-TG/image-build wiring as the single-pod fixture for the same bot —
/// both share the ONE cached image spec keyed by `Base.AppImageName`).
type MultiPodContainerConfig =
    { Base: BotContainerConfig
      /// Number of bot app instances to start from the same built image. Default 2.
      InstanceCount: int }

/// Shared container lifecycle for MULTI-instance bot integration tests: one network, one
/// postgres+flyway, one FakeTgApi(+OCR), N app containers from ONE cached image spec (see
/// ContainerHelpers.getOrCreateImageSpec — a per-instance rebuild of the same tag races and
/// 409s on podman, same hazard BotContainerBase avoids for the single-pod case).
///
/// Deliberately a SIBLING of BotContainerBase, not a subclass or a retrofit: BotContainerBase's
/// singular botContainer/botHttp fields, SendUpdate/RestartBotApp, and "bot"-named log dump are
/// depended on by the 10 existing single-pod fixtures and must stay byte-identical, so this type
/// owns its own N-element container/client arrays instead.
[<AbstractClass>]
type MultiPodContainerBase(config: MultiPodContainerConfig) =
    let cfg = config.Base
    let n = config.InstanceCount
    let solutionDir = CommonDirectoryPath.GetSolutionDirectory()
    let solutionDirPath = solutionDir.DirectoryPath
    let dbAlias = cfg.MigrationsSubdir + "-db"
    let fakeAlias = "fake-tg-api"
    let fakeAzureAlias = "fake-azure-ocr"
    let pgImage = cfg.PostgresImage

    let internalConnectionString =
        $"Server={dbAlias};Database={cfg.DbName};Port=5432;User Id={cfg.DbUser};Password={cfg.DbPassword};Include Error Detail=true;Minimum Pool Size=1;Maximum Pool Size=20;Max Auto Prepare=100;Auto Prepare Min Usages=1;Trust Server Certificate=true;"

    let mutable botHttps: HttpClient[] = [||]
    let mutable fakeTgHttp: HttpClient = null
    let mutable fakeAzureHttp: HttpClient = null
    let mutable publicConnectionString: string = null
    let mutable adminConnectionString: string = null
    let mutable testArtifactsDir: string = null

    let network = createNetwork()
    let dbContainer = createPostgresContainer network dbAlias pgImage
    let migrationsPath = Path.Combine(solutionDirPath, "src", cfg.MigrationsSubdir, "migrations")
    let flywayContainer = createFlywayContainer network migrationsPath dbAlias cfg.DbName dbContainer

    let fakeTgImage, fakeTgBuildLogger =
        getOrCreateImageSpec $"{cfg.AppImageName}-fake-tg-api" (fun () ->
            buildImageSpec solutionDir "./tests/Dockerfile.fake" $"{cfg.AppImageName}-fake-tg-api" true true ["FAKE_PROJECT", "FakeTgApi"; "FAKE_PORT", "8080"])
    let fakeTgContainer = createFakeTgApiContainer fakeTgImage network fakeAlias

    let fakeAzureImage, fakeAzureBuildLogger =
        getOrCreateImageSpec $"{cfg.AppImageName}-fake-azure-ocr" (fun () ->
            buildImageSpec solutionDir "./tests/Dockerfile.fake" $"{cfg.AppImageName}-fake-azure-ocr" true true ["FAKE_PROJECT", "FakeAzureOcrApi"; "FAKE_PORT", "8081"])
    let fakeAzureContainer = createFakeAzureOcrContainer fakeAzureImage network fakeAzureAlias

    // Same cache key (cfg.AppImageName) as BotContainerBase — a multi-pod fixture and a
    // single-pod fixture for the SAME bot reuse one build, never race a second one.
    let botImage, botBuildLogger =
        getOrCreateImageSpec cfg.AppImageName (fun () ->
            let logger = StringLogger()
            let img =
                ImageFromDockerfileBuilder()
                    .WithDockerfileDirectory(solutionDir, String.Empty)
                    .WithDockerfile("./src/Dockerfile.bot")
                    .WithName(cfg.AppImageName)
                    .WithBuildArgument("BOT_PROJECT", cfg.BotProject)
                    .WithBuildArgument("RESOURCE_REAPER_SESSION_ID", ResourceReaper.DefaultSessionId.ToString("D"))
                    .WithDeleteIfExists(true)
                    .WithCleanUp(true)
                    .WithLogger(logger)
                    .Build()
            (img, logger))

    let makeBotContainer () =
        let mutable b =
            ContainerBuilder(botImage)
                .WithNetwork(network)
                .WithPortBinding(80, true)
                .WithEnvironment("DATABASE_URL", internalConnectionString)
                .WithEnvironment("ASPNETCORE_HTTP_PORTS", "80")
                .DependsOn(flywayContainer)
                .DependsOn(fakeTgContainer)
        for (key, value) in cfg.AppEnvVars do
            b <- b.WithEnvironment(key, value)
        if cfg.OcrEnabled then
            b <- b.DependsOn(fakeAzureContainer)
        b.WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(80))
            .Build()

    let botContainers: IContainer[] = Array.init n (fun _ -> makeBotContainer ())

    /// Override to seed the database after migrations run and BEFORE any instance starts —
    /// runs exactly once, same lifecycle position as BotContainerBase.SeedDatabase.
    abstract SeedDatabase: connString: string -> Task
    default _.SeedDatabase(_) = Task.CompletedTask

    /// Override to run additional setup after ALL instances are started and HTTP clients ready.
    abstract AfterStart: unit -> Task
    default _.AfterStart() = Task.CompletedTask

    interface IAsyncLifetime with
        member this.InitializeAsync() =
            ValueTask(task {
                testArtifactsDir <- Path.Combine(solutionDirPath, "test-artifacts", $"{cfg.BotProject}.Tests", this.GetType().Name)
                do! dbContainer.StartAsync()

                let mappedPort = dbContainer.GetMappedPublicPort(5432)
                let connStr (user: string) (password: string) =
                    $"Server=127.0.0.1;Database={cfg.DbName};Port={mappedPort};User Id={user};Password={password};Include Error Detail=true;Timeout=120;Command Timeout=120;Keepalive=30;"
                publicConnectionString <- connStr cfg.DbUser cfg.DbPassword
                adminConnectionString <- connStr "admin" "admin"

                let initSql = File.ReadAllText(Path.Combine(solutionDirPath, "src", cfg.MigrationsSubdir, "init.sql"))
                let! initResult = dbContainer.ExecScriptAsync(initSql)
                if initResult.Stderr <> "" then failwith initResult.Stderr

                do! flywayContainer.StartAsync()
                let! flywayExitCode = flywayContainer.GetExitCodeAsync()
                if flywayExitCode <> 0L then
                    let! struct (stdout, stderr) = flywayContainer.GetLogsAsync()
                    failwith $"Flyway migrations failed (exit code {flywayExitCode})\n=== STDOUT ===\n{stdout}\n=== STDERR ===\n{stderr}"

                do! this.SeedDatabase(publicConnectionString)

                let botBuildTask = buildImageOncePerProcess cfg.AppImageName testArtifactsDir "bot" botImage botBuildLogger
                let fakeTgBuildTask = buildImageOncePerProcess $"{cfg.AppImageName}-fake-tg-api" testArtifactsDir "fake-tg-api" fakeTgImage fakeTgBuildLogger
                let fakeAzureBuildTask =
                    if cfg.OcrEnabled then buildImageOncePerProcess $"{cfg.AppImageName}-fake-azure-ocr" testArtifactsDir "fake-azure-ocr" fakeAzureImage fakeAzureBuildLogger
                    else Task.CompletedTask
                do! Task.WhenAll([| botBuildTask; fakeTgBuildTask; fakeAzureBuildTask |])

                do! fakeTgContainer.StartAsync()
                if cfg.OcrEnabled then
                    do! fakeAzureContainer.StartAsync()

                // Start every instance in parallel from the one already-built image.
                do! Task.WhenAll(botContainers |> Array.map (fun c -> c.StartAsync()))

                botHttps <-
                    botContainers
                    |> Array.map (fun c ->
                        let http = new HttpClient(BaseAddress = Uri($"http://127.0.0.1:{c.GetMappedPublicPort(80)}"))
                        http.Timeout <- TimeSpan.FromSeconds(15.0)
                        http.DefaultRequestHeaders.Add("X-Telegram-Bot-Api-Secret-Token", cfg.SecretToken)
                        http)

                fakeTgHttp <- new HttpClient(BaseAddress = Uri($"http://127.0.0.1:{fakeTgContainer.GetMappedPublicPort(8080)}"))
                fakeTgHttp.Timeout <- TimeSpan.FromSeconds(5.0)

                if cfg.OcrEnabled then
                    fakeAzureHttp <- new HttpClient(BaseAddress = Uri($"http://127.0.0.1:{fakeAzureContainer.GetMappedPublicPort(8081)}"))
                    fakeAzureHttp.Timeout <- TimeSpan.FromSeconds(5.0)

                do! this.AfterStart()
            } :> Task)

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(task {
                for i in 0 .. n - 1 do
                    let! _ = dumpContainerLogs testArtifactsDir $"bot-{i}" botContainers[i]
                    ()
                let! _ = dumpContainerLogs testArtifactsDir "fake-tg-api" fakeTgContainer
                if cfg.OcrEnabled then
                    let! _ = dumpContainerLogs testArtifactsDir "fake-azure-ocr" fakeAzureContainer
                    ()
                let! _ = dumpContainerLogs testArtifactsDir "flyway" flywayContainer
                let! _ = dumpContainerLogs testArtifactsDir "postgres" dbContainer

                for http in botHttps do
                    if not (isNull http) then http.Dispose()
                if not (isNull fakeTgHttp) then fakeTgHttp.Dispose()
                if not (isNull fakeAzureHttp) then fakeAzureHttp.Dispose()
                do! Task.WhenAll(botContainers |> Array.map (fun c -> c.DisposeAsync().AsTask()))
                do! fakeTgContainer.DisposeAsync()
                if cfg.OcrEnabled then
                    do! fakeAzureContainer.DisposeAsync()
                do! flywayContainer.DisposeAsync()
                do! dbContainer.DisposeAsync()
            } :> Task)

    // ── Exposed clients ─────────────────────────────────────────────────
    member _.InstanceCount = n
    member _.BotHttp(i: int) = botHttps[i]
    member _.FakeTgHttp = fakeTgHttp
    member _.FakeAzureHttp = fakeAzureHttp
    member _.DbConnectionString = publicConnectionString
    member _.AdminDbConnectionString = adminConnectionString
    member _.OcrEnabled = cfg.OcrEnabled

    // ── Shared helpers ──────────────────────────────────────────────────

    member _.ClearFakeCalls() =
        task {
            let! _ = fakeTgHttp.DeleteAsync("/test/calls")
            return ()
        }

    member _.GetFakeCalls(method: string) =
        task {
            let! resp = fakeTgHttp.GetFromJsonAsync<FakeCall array>($"/test/calls?method={method}")
            return resp
        }

    /// Same convention as BotContainerBase.SetChatMemberStatus — FakeTgApi is shared, so this
    /// applies to every instance's membership checks at once.
    member _.SetChatMemberStatus(userId: int64, status: string) =
        task {
            let payload: ChatMemberMock = { userId = userId; status = status }
            let! _ = fakeTgHttp.PostAsJsonAsync("/test/mock/chatMember", payload)
            return ()
        }

    /// Sends a Telegram update to instance `i`'s webhook route. FakeTgApi is shared across all
    /// instances (ApiCallLog has no instance-identity field) — assert on aggregate call content.
    member _.SendUpdateTo(i: int, update: Funogram.Telegram.Types.Update) =
        task {
            let json = Encoding.UTF8.GetString(Funogram.Tools.toJson update)
            use content = new StringContent(json, Encoding.UTF8, "application/json")
            return! botHttps[i].PostAsync(cfg.WebhookRoute, content)
        }

    member _.GetBotLogs(i: int) =
        task {
            let! (stdout, stderr) = botContainers[i].GetLogsAsync()
            return $"=== STDOUT ===\n{stdout}\n=== STDERR ===\n{stderr}"
        }

    /// GET /test/settings/dump from instance `i` — raw JSON (secret fields are `{present:bool}`,
    /// never a comparable value; parse with JsonDocument per AGENTS.md's Cyrillic/JSON rule).
    member _.GetSettingsDump(i: int) =
        task {
            let! resp = botHttps[i].GetAsync("/test/settings/dump")
            resp.EnsureSuccessStatusCode() |> ignore
            return! resp.Content.ReadAsStringAsync()
        }

    /// Advances EVERY instance's FakeTimeProvider by the same `ms`, in lockstep. Instances'
    /// clocks otherwise drift independently, which breaks cross-pod logic that compares
    /// this-instance `now` against another instance's DB-persisted timestamp (e.g. CouponHubBot's
    /// batch debounce). Never call the single-instance `/test/clock/advance` route directly on
    /// one instance of a multi-pod fixture — always go through this. Requires TEST_MODE=true.
    member _.AdvanceAllClocks(ms: int) =
        task {
            for i in 0 .. n - 1 do
                use content = new StringContent("", Encoding.UTF8, "application/json")
                let! resp = botHttps[i].PostAsync($"/test/clock/advance?ms={ms}", content)
                resp.EnsureSuccessStatusCode() |> ignore
        }
