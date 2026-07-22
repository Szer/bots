/// Standalone harness self-check (`dotnet run -- selfcheck`): proves the full
/// plumbing chain works WITHOUT the MTProto user client:
/// dev DB -> built bot -> ngrok tunnel -> bot process healthy -> setWebhook ->
/// getWebhookInfo -> public /healthz through the tunnel -> deleteWebhook.
module AlitaBot.RealTests.SelfCheck

open System
open System.IO
open System.Net.Http

let runAsync () =
    task {
        let env = RealEnv.load ()
        let step (n: int) (msg: string) = printfn "[selfcheck %d/7] %s" n msg

        if not env.HasCore then
            eprintfn
                "selfcheck: core env incomplete in %s (need ALITA_NGROK_DOMAIN, ALITA_TEST_BOT_TOKEN, ALITA_TEST_BOT_USERNAME, ALITA_WEBHOOK_SECRET, ALITA_TEST_CHAT_ID)"
                RealEnv.envFilePath

            return 1
        else
            step 1 "dev DB: docker compose up + wait for seed"
            do! DevDb.upAsync ()
            do! DevDb.applyRealSettingsAsync env

            step 2 $"bot build present: {BotProcess.DllPath}"

            if not (File.Exists BotProcess.DllPath) then
                failwith "AlitaBot.dll missing — run `make alita-build` first"

            step 3 $"ngrok tunnel https://{env.NgrokDomain} -> 127.0.0.1:5010"
            use! tunnel = NgrokTunnel.ConnectAsync(env.NgrokDomain, env.NgrokAuthtoken)
            printfn "  tunnel up: %s" tunnel.PublicUrl

            step 4 "bot process on 127.0.0.1:5010, waiting for /healthz"
            use! _bot = BotProcess.StartAsync env

            step 5 "setWebhook + getWebhookInfo verification"
            use! webhook = TelegramWebhook.SetAsync env
            let! info = webhook.GetInfoAsync()
            let url = info.GetProperty("url").GetString()
            let pending = info.GetProperty("pending_update_count").GetInt32()

            let lastError =
                match info.TryGetProperty "last_error_message" with
                | true, v -> string v
                | _ -> "none"

            printfn "  webhook url = %s, pending = %d, last_error = %s" url pending lastError

            if pending <> 0 then
                failwith $"expected 0 pending updates after drop_pending_updates, got {pending}"

            step 6 $"public healthz through the tunnel: https://{env.NgrokDomain}/healthz"
            use http = new HttpClient(Timeout = TimeSpan.FromSeconds 15.)
            // ngrok free-tier interstitial triggers on browser user agents; be explicit.
            http.DefaultRequestHeaders.Add("ngrok-skip-browser-warning", "1")
            let! body = http.GetStringAsync $"https://{env.NgrokDomain}/healthz"

            if body <> "OK" then
                failwith $"public /healthz returned '{body}', expected 'OK'"

            printfn "  public /healthz -> %s" body

            step 7 "teardown: deleteWebhook + stop bot + stop tunnel (on dispose)"
            printfn "selfcheck PASSED"
            return 0
    }
