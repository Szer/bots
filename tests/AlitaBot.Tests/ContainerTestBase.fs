namespace AlitaBot.Tests

open System.Threading.Tasks
open BotTestInfra
open Dapper
open Npgsql

module AlitaTestConfig =
    let secret = "OUR_SECRET"
    let targetChatId = -4242L
    let botUsername = "alita_test_bot"

    let config: BotContainerConfig =
        { BotProject = "AlitaBot"
          MigrationsSubdir = "alita-bot"
          DbName = "alita_bot"
          DbUser = "alita_bot_service"
          DbPassword = "alita_bot_service"
          AppImageName = "alita-bot-test"
          // OcrEnabled brings up the shared FakeAzureOcrApi container — AlitaBot uses
          // its /openai/ chat-completions routes (incl. SSE) as the fake LLM backend.
          OcrEnabled = true
          SecretToken = secret
          WebhookRoute = "/bot"
          AppEnvVars = [
              "BOT_TELEGRAM_TOKEN", "123:456"
              "BOT_AUTH_TOKEN", secret
              "TELEGRAM_API_URL", "http://fake-tg-api:8080"
              "AZURE_FOUNDRY_KEY", "test-key"
              "TEST_MODE", "true"
          ] }

type AlitaTestContainers() =
    inherit BotContainerBase(AlitaTestConfig.config)

    override _.SeedDatabase(connString: string) =
        task {
            use conn = new NpgsqlConnection(connString)
            do! conn.OpenAsync()

            let settings = [
                "TARGET_CHAT_IDS",         string AlitaTestConfig.targetChatId, "FREE_FORM", "telegram"
                "BOT_USERNAME",            AlitaTestConfig.botUsername,         "FREE_FORM", "telegram"
                "SYSTEM_PROMPT",           "You are a test bot.",               "FREE_FORM", "llm"
                "RESPONDER_MODE",          "echo",                              "FREE_FORM", "llm"
                "STREAM_MODE",             "edit",                              "FREE_FORM", "llm"
                "CONTEXT_WINDOW_MESSAGES", "30",                                "FREE_FORM", "llm"
                "AZURE_FOUNDRY_ENDPOINT",  "http://fake-azure-ocr:8081",        "FREE_FORM", "llm"
                "LLM_DEPLOYMENT",          "alita-gpt-5-mini",                  "FREE_FORM", "llm"
                "STT_DEPLOYMENT",          "alita-stt",                         "FREE_FORM", "llm"
                "TTS_DEPLOYMENT",          "alita-tts",                         "FREE_FORM", "llm"
                "VOICE_TRANSCRIBE_ENABLED", "true",                             "FEATURE_FLAG", "llm"
                "LLM_PRICING",             """{"gpt-5-mini":{"input_per_1m":0.25,"output_per_1m":2.00}}""", "JSON_BLOB", "llm"
                "TEST_MODE",               "true",                              "FEATURE_FLAG", "diagnostics"
            ]
            for (key, value, typ, group) in settings do
                do! conn.ExecuteAsync(
                        "INSERT INTO bot_setting(key,value,type,feature_group) VALUES(@k,@v,@t,@g)
                         ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, type = EXCLUDED.type, feature_group = EXCLUDED.feature_group",
                        {| k = key; v = value; t = typ; g = group |})
                    :> Task
        }

    member _.TargetChatId = AlitaTestConfig.targetChatId
    member _.BotUsername = AlitaTestConfig.botUsername
    member this.Bot = this.BotHttp
    member this.TelegramApi = this.FakeTgHttp

    /// Upserts a bot_setting value; call ReloadSettings() afterwards to apply it.
    member this.SetBotSetting(key: string, value: string) =
        task {
            use conn = new NpgsqlConnection(this.DbConnectionString)
            //language=postgresql
            let sql =
                """
INSERT INTO bot_setting(key, value, type, feature_group)
VALUES(@key, @value, 'FREE_FORM', 'RUNTIME')
ON CONFLICT (key) DO UPDATE SET value = @value
"""
            let! _ = conn.ExecuteAsync(sql, {| key = key; value = value |})
            return ()
        }

    member this.ReloadSettings() =
        task {
            let! resp = this.BotHttp.PostAsync("/reload-settings", null)
            resp.EnsureSuccessStatusCode() |> ignore
        }

    member this.QuerySingleOrDefault<'t>(sql: string, param: obj) =
        task {
            use conn = new NpgsqlConnection(this.DbConnectionString)
            return! conn.QuerySingleOrDefaultAsync<'t>(sql, param)
        }

    member this.Query<'t>(sql: string, param: obj) =
        task {
            use conn = new NpgsqlConnection(this.DbConnectionString)
            let! rows = conn.QueryAsync<'t>(sql, param)
            return rows |> Seq.toArray
        }
