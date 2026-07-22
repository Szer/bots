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
          ]
          // Slice 5a: message_embedding needs pgvector (CREATE EXTENSION vector, V3
          // migration). Scoped to Alita alone — vahter/coupon keep postgres:17.10.
          PostgresImage = "pgvector/pgvector:pg17" }

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
                "EMBEDDING_DEPLOYMENT",    "alita-text-embedding-3-small",      "FREE_FORM", "llm"
                "STT_DEPLOYMENT",          "alita-stt",                         "FREE_FORM", "llm"
                "TTS_DEPLOYMENT",          "alita-tts",                         "FREE_FORM", "llm"
                "VOICE_TRANSCRIBE_ENABLED", "true",                             "FEATURE_FLAG", "llm"
                "VISION_ENABLED",          "true",                              "FEATURE_FLAG", "llm"
                "VISION_DETAIL",           "low",                               "FREE_FORM", "llm"
                "IMAGE_DEPLOYMENT",        "alita-image",                       "FREE_FORM", "llm"
                "IMAGE_GEN_ENABLED",       "true",                              "FEATURE_FLAG", "llm"
                "IMAGE_SIZE",              "1024x1024",                         "FREE_FORM", "llm"
                "IMAGE_QUALITY",           "medium",                            "FREE_FORM", "llm"
                "LLM_PRICING",             """{"gpt-5-mini":{"input_per_1m":0.25,"output_per_1m":2.00},"alita-image":{"per_image_low":0.02,"per_image_medium":0.04,"per_image_high":0.08}}""", "JSON_BLOB", "llm"
                // Two entries so /model switch tests have a real allowlisted alternative
                // to switch to (distinct from the initial LLM_DEPLOYMENT below).
                "MODEL_ALLOWLIST",         """["alita-gpt-5-mini","alita-gpt-5-mini-2"]""", "JSON_BLOB", "llm"
                "SUMMARY_PROMPT",          "Summarize the chat discussion below, by topic, noting who said what. Be brief.", "FREE_FORM", "llm"
                "TEST_MODE",               "true",                              "FEATURE_FLAG", "diagnostics"
                // Slice 5a: memory foundation + /ask semantic search.
                "EMBED_MESSAGES",          "true",                              "FEATURE_FLAG", "llm"
                "EMBEDDING_MIN_CHARS",     "3",                                 "FREE_FORM", "llm"
                "ASK_TOP_K",               "8",                                 "FREE_FORM", "llm"
                "ASK_SIM_FLOOR",           "0.5",                               "FREE_FORM", "llm"
                "ASK_PROMPT",              "Answer the question using only the chat quotes below. Cite who said what and when. If nothing relevant is found, say so plainly.", "FREE_FORM", "llm"
                // Slice 5b: per-person dossiers + nightly fact extraction.
                "DOSSIER_ENABLED",        "true",                              "FEATURE_FLAG", "llm"
                "DOSSIER_RECALL_K",       "5",                                 "FREE_FORM", "llm"
                "DOSSIER_SIM_FLOOR",      "0.60",                              "FREE_FORM", "llm"
                "EXTRACT_PROMPT",         "Extract new short facts about this person from their recent messages. Reply with ONLY a JSON array of short fact strings, e.g. [\"likes cats\"]. If nothing new, reply [].", "FREE_FORM", "llm"
                "MERGE_PROMPT",           "Merge the new facts into the existing dossier summary. Reply with the updated summary text only, max 250 words.", "FREE_FORM", "llm"
                // Slice 6: persona + rewriter + outcome router + MarkdownV2 rendering.
                "REWRITER_ENABLED",       "false",                             "FEATURE_FLAG", "llm"
                "REWRITER_PROMPT",        "Rewrite the following reply as if a real human chat member wrote it: strip assistant-isms, shorten where possible, keep the meaning and all facts. Reply with only the rewritten text.", "FREE_FORM", "llm"
                "OUTCOME_WEIGHTS",        """{"reply":100,"silence":0,"emoji":0}""", "JSON_BLOB", "llm"
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

    /// Truncates every Slice-5b memory/dossier table plus message_log/message_embedding
    /// (owner-only op, via AdminDbConnectionString — the service role has no DELETE grant
    /// broad enough for this) — DossierTests' nightly-job tests need a clean "active
    /// users" population, otherwise every user_id ever seeded by an earlier test class in
    /// this shared, `DisableTestParallelization = true` assembly fixture (see Program.fs)
    /// shows up as "active in the last 24h" too (TEST_MODE's FakeTimeProvider is never
    /// advanced across the whole assembly run, so every seeded message's `sent_at` shares
    /// one frozen timestamp for the entire test run).
    member this.TruncateMemoryTables() =
        task {
            use conn = new NpgsqlConnection(this.AdminDbConnectionString)
            do! conn.OpenAsync()
            let! _ =
                conn.ExecuteAsync(
                    "TRUNCATE message_log, message_embedding, interaction_memory, person_dossier, memory_opt_out RESTART IDENTITY CASCADE;")
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
