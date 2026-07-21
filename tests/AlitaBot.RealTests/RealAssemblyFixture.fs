namespace AlitaBot.RealTests

open System
open System.Threading.Tasks
open Xunit

/// Assembly-wide fixture composing: dev DB (compose) -> build check -> ngrok
/// tunnel -> bot process -> Telegram webhook -> MTProto user client (only when
/// credentials + session exist). With incomplete core env nothing is started
/// and every test self-skips via the Skip* helpers.
type RealAssemblyFixture() =

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
                    do! DevDb.upAsync ()
                    do! DevDb.applyRealSettingsAsync env

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
