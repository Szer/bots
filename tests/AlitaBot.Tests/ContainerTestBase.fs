namespace AlitaBot.Tests

open System
open System.Globalization
open System.Threading.Tasks
open BotTestInfra
open Dapper
open Npgsql
open Xunit

module private AlitaTestConfig =
    let secret          = "OUR_SECRET"
    let targetChatId    = -42L
    let fakeFoundryAlias = "fake-azure-ocr"  // reuses the shared container alias
    let fixedUtcNow     =
        DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture,
                             DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal)

    let makeConfig () : BotContainerConfig =
        { BotProject          = "AlitaBot"
          MigrationsSubdir    = "alita-bot"
          DbName              = "alita_bot"
          DbUser              = "alita_bot_service"
          DbPassword          = "alita_bot_service"
          AppImageName        = "alita-bot-test"
          OcrEnabled          = true   // reuses the fakeAzureOcr container slot for FakeAzureFoundryApi
          FakeAiProjectOverride = Some "FakeAzureFoundryApi"
          SecretToken         = secret
          WebhookRoute        = "/bot"
          AppEnvVars          = [
              "BOT_TELEGRAM_TOKEN",          "123:456"
              "BOT_AUTH_TOKEN",              secret
              "TELEGRAM_API_URL",            "http://fake-tg-api:8080"
              "AZURE_FOUNDRY_ENDPOINT",      $"http://{fakeFoundryAlias}:8081"
              "AZURE_FOUNDRY_KEY",           "fake-key"
              "BOT_FIXED_UTC_NOW",           fixedUtcNow.ToString("o")
          ] }

[<AbstractClass>]
type AlitaTestContainers() =
    inherit BotContainerBase(AlitaTestConfig.makeConfig())

    override this.SeedDatabase(connString: string) =
        task {
            use conn = new NpgsqlConnection(connString)
            do! conn.OpenAsync()

            let settings = [
                "TARGET_CHAT_ID",          string AlitaTestConfig.targetChatId,  "FREE_FORM",    "CORE"
                "BOT_USERNAME",            "Alita",                               "FREE_FORM",    "CORE"
                "TEST_MODE",               "true",                                "FEATURE_FLAG", "CORE"
                "LAYER3_WEIGHT_SILENCE",   "0",                                   "FREE_FORM",    "LLM"
                "LAYER3_WEIGHT_USUAL",     "100",                                 "FREE_FORM",    "LLM"
                "LAYER3_WEIGHT_EMOJI_MEME","0",                                   "FREE_FORM",    "LLM"
                "LAYER3_WEIGHT_CHAOS",     "0",                                   "FREE_FORM",    "LLM"
                "NEWS_SOURCE_URLS",        "[]",                                  "JSON_BLOB",    "NEWS"
                "PROACTIVE_POST_PROBABILITY", "0.0",                              "FREE_FORM",    "PROACTIVE"
                "MESSAGE_LOG_RETENTION_DAYS", "365",                              "FREE_FORM",    "CLEANUP"
            ]
            for (key, value, typ, group) in settings do
                do! conn.ExecuteAsync(
                        "INSERT INTO bot_setting(key,value,type,feature_group) VALUES(@k,@v,@t,@g)",
                        {| k = key; v = value; t = typ; g = group |})
                    :> Task
        }

    member _.TargetChatId    = AlitaTestConfig.targetChatId
    member this.Bot          = this.BotHttp
    member this.TelegramApi  = this.FakeTgHttp
    member this.FoundryApi   = this.FakeAzureHttp   // FakeAzureFoundryApi on port 8081
    member _.FixedUtcNow     = AlitaTestConfig.fixedUtcNow

    member this.QuerySingle<'t>(sql: string, param: obj) =
        task {
            use conn = new NpgsqlConnection(this.DbConnectionString)
            return! conn.QuerySingleAsync<'t>(sql, param)
        }

    member this.QuerySingleOrDefault<'t>(sql: string, param: obj) =
        task {
            use conn = new NpgsqlConnection(this.DbConnectionString)
            return! conn.QuerySingleOrDefaultAsync<'t>(sql, param)
        }

    member this.Execute(sql: string, param: obj) =
        task {
            use conn = new NpgsqlConnection(this.DbConnectionString)
            return! conn.ExecuteAsync(sql, param)
        }

    member private this.SetLayer3Weights(silence, usual, emoji, chaos) =
        task {
            for (key, value) in
                [ "LAYER3_WEIGHT_SILENCE",    string silence
                  "LAYER3_WEIGHT_USUAL",      string usual
                  "LAYER3_WEIGHT_EMOJI_MEME", string emoji
                  "LAYER3_WEIGHT_CHAOS",      string chaos ] do
                do! this.Execute("UPDATE bot_setting SET value=@v WHERE key=@k", {| k = key; v = value |}) :> Task
            let! _ = this.BotHttp.PostAsync("/reload-settings", null)
            return ()
        }

    /// Sets Layer 3 to always choose silence (weights: silence=100, rest=0).
    member this.SetLayer3Silence() = this.SetLayer3Weights(100, 0, 0, 0)

    /// Resets Layer 3 to the default test state (usual=100, rest=0).
    member this.ResetLayer3Usual() = this.SetLayer3Weights(0, 100, 0, 0)

    /// Configures the FakeAzureFoundryApi to return a specific completion for a deployment.
    member this.SetFoundryCompletion(deployment: string, content: string) =
        task {
            let payload = System.Text.Json.JsonSerializer.Serialize({| deployment = deployment; content = content |})
            use body = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json")
            let! _ = this.FoundryApi.PostAsync("/test/mock/completion", body)
            return ()
        }

type DefaultAlitaTestContainers() =
    inherit AlitaTestContainers()
