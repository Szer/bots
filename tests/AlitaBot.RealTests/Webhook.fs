namespace AlitaBot.RealTests

open System
open System.Net.Http
open System.Net.Http.Json
open System.Text.Json
open System.Threading.Tasks

/// Registers https://{domain}/bot as the test bot's webhook (with secret token,
/// pending updates dropped, messages only) and removes it on dispose.
/// Error paths are sanitized so the bot token never leaks into logs.
type TelegramWebhook private (http: HttpClient, botToken: string, webhookUrl: string) =

    static let sanitize (botToken: string) (message: string) = message.Replace(botToken, "***")

    static let callApi (http: HttpClient) (botToken: string) (method_: string) (payload: obj) =
        task {
            try
                let! resp = http.PostAsJsonAsync($"https://api.telegram.org/bot{botToken}/{method_}", payload)
                let! body = resp.Content.ReadAsStringAsync()
                let doc = JsonDocument.Parse body

                if not (doc.RootElement.GetProperty("ok").GetBoolean()) then
                    failwith $"Telegram {method_} failed: {sanitize botToken body}"

                return doc
            with :? HttpRequestException as e ->
                return failwith $"Telegram {method_} failed: {sanitize botToken e.Message}"
        }

    member _.Url = webhookUrl

    /// getWebhookInfo `result` element (url, pending_update_count, last_error_message, ...).
    member _.GetInfoAsync() =
        task {
            use! doc = callApi http botToken "getWebhookInfo" {| |}
            return doc.RootElement.GetProperty("result").Clone()
        }

    member _.DeleteAsync() =
        task {
            use! _doc = callApi http botToken "deleteWebhook" {| drop_pending_updates = true |}
            return ()
        }

    static member SetAsync(env: RealEnv) =
        task {
            let http = new HttpClient(Timeout = TimeSpan.FromSeconds 15.)
            let url = $"https://{env.NgrokDomain}/bot"

            let payload =
                {| url = url
                   secret_token = env.WebhookSecret
                   drop_pending_updates = true
                   allowed_updates = [| "message" |] |}

            use! _doc = callApi http env.BotToken "setWebhook" payload

            // Verify Telegram actually stored our URL.
            let webhook = new TelegramWebhook(http, env.BotToken, url)
            let! info = webhook.GetInfoAsync()
            let stored = info.GetProperty("url").GetString()

            if stored <> url then
                failwith $"getWebhookInfo reports url '{stored}', expected '{url}'"

            return webhook
        }

    interface IAsyncDisposable with
        member this.DisposeAsync() =
            task {
                try
                    do! this.DeleteAsync()
                with _ ->
                    () // best effort — never mask the test failure that got us here

                http.Dispose()
            }
            |> ValueTask
