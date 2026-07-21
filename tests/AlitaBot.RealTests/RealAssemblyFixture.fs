namespace AlitaBot.RealTests

open System
open System.Net.Http
open System.Threading.Tasks
open Xunit

/// Assembly-wide fixture composing: dev DB (compose) -> build check -> ngrok
/// tunnel -> bot process -> Telegram webhook -> MTProto user client (only when
/// credentials + session exist). With incomplete core env nothing is started
/// and every test self-skips via the Skip* helpers.
///
/// ALITA_REAL_MODE=remote (CI only; default is local, zero behavior change):
/// the bot is already deployed to AKS by the workflow and Postgres is already
/// reachable at localhost:15433 via a `kubectl port-forward` the workflow
/// started — this fixture skips DevDb-compose/NgrokTunnel/BotProcess entirely
/// and just probes the public URL's /healthz before registering the webhook.
type RealAssemblyFixture() =

    /// Polls url until it 200s or the timeout elapses. Local mode gets the
    /// same wait via BotProcess.StartAsync; remote mode needs its own since
    /// there's no child process here to gate on.
    static let waitHealthyAsync (url: string) =
        task {
            use http = new HttpClient(Timeout = TimeSpan.FromSeconds 5.)
            let timeout = TimeSpan.FromSeconds 60.
            let deadline = DateTime.UtcNow + timeout
            let mutable healthy = false

            while not healthy && DateTime.UtcNow < deadline do
                try
                    let! resp = http.GetAsync url
                    healthy <- resp.IsSuccessStatusCode
                with _ ->
                    ()

                if not healthy then
                    do! Task.Delay 1000

            if not healthy then
                failwith $"{url} did not become healthy within {timeout.TotalSeconds}s"
        }

    /// Remote mode only — see RealEnv.ReloadSettingsUrl: the pod was already
    /// running before applyRealSettingsAsync upserted TARGET_CHAT_IDS etc.,
    /// so its cached IOptions<BotConfiguration> is stale until this is called.
    static let reloadSettingsAsync (env: RealEnv) =
        task {
            use http = new HttpClient(Timeout = TimeSpan.FromSeconds 10.)
            http.DefaultRequestHeaders.Add("X-Telegram-Bot-Api-Secret-Token", env.WebhookSecret)
            let! resp = http.PostAsync(env.ReloadSettingsUrl, null)

            if not resp.IsSuccessStatusCode then
                let! body = resp.Content.ReadAsStringAsync()
                failwith $"POST {env.ReloadSettingsUrl} -> {int resp.StatusCode}: {body}"
        }

    let env = RealEnv.load ()

    let mutable tunnel: NgrokTunnel option = None
    let mutable bot: BotProcess option = None
    let mutable webhook: TelegramWebhook option = None
    let mutable userClient: TgUserClient option = None

    member _.Env = env

    /// Npgsql connection string for direct DB assertions (message_log etc.).
    member _.DbConnectionString = DevDb.connectionString

    member _.Webhook =
        match webhook with
        | Some w -> w
        | None -> failwith "webhook is not up — fixture skipped initialization (incomplete env?)"

    member _.UserClient =
        match userClient with
        | Some c -> c
        | None -> failwith "user client is not up — call SkipUnlessUserClient() first"

    member _.SkipUnlessCore() =
        if not env.HasCore then
            Assert.Skip
                $"core credentials missing in {RealEnv.envFilePath} (ngrok domain / bot token / username / webhook secret / chat id)"

    member this.SkipUnlessUserClient() =
        this.SkipUnlessCore()

        if not env.HasUserClient then
            Assert.Skip
                "MTProto user client unavailable — fill ALITA_TG_API_ID/HASH/PHONE in ~/.alita-test/env and run `make tg-login`"

    interface IAsyncLifetime with
        member _.InitializeAsync() : ValueTask =
            task {
                if env.HasCore then
                    if not env.IsRemote then
                        do! DevDb.upAsync ()

                    do! DevDb.applyRealSettingsAsync env

                    if env.IsRemote then
                        do! waitHealthyAsync env.HealthUrl
                        // Pod was already running before the upsert above —
                        // make it pick up TARGET_CHAT_IDS/BOT_USERNAME/etc. now.
                        do! reloadSettingsAsync env
                    else
                        let! t = NgrokTunnel.ConnectAsync(env.NgrokDomain, env.NgrokAuthtoken)
                        tunnel <- Some t

                        let! b = BotProcess.StartAsync env
                        bot <- Some b

                    let! w = TelegramWebhook.SetAsync env
                    webhook <- Some w

                    if env.HasUserClient then
                        let c = new TgUserClient(env.TgApiId, env.TgApiHash, env.TgSessionPath, env.TgPhone)
                        let! _ = c.LoginAsync()
                        userClient <- Some c
            }
            |> ValueTask

        member _.DisposeAsync() : ValueTask =
            task {
                // Reverse order; each step is best-effort so teardown always completes.
                for disposable in
                    [ webhook |> Option.map (fun w -> w :> IAsyncDisposable)
                      userClient |> Option.map (fun c -> c :> IAsyncDisposable)
                      bot |> Option.map (fun b -> b :> IAsyncDisposable)
                      tunnel |> Option.map (fun t -> t :> IAsyncDisposable) ]
                    |> List.choose id do
                    try
                        do! disposable.DisposeAsync()
                    with _ ->
                        ()
            }
            |> ValueTask
