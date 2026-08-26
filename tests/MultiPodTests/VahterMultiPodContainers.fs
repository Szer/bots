namespace MultiPodTests

open System
open System.IO
open System.Threading.Tasks
open BotTestInfra
open DotNet.Testcontainers.Builders
open DotNet.Testcontainers.Configurations
open DotNet.Testcontainers.Containers
open DotNet.Testcontainers.Images
open Npgsql
open Dapper

/// 2-instance VahterBanBot fixture. ML_ENABLED mirrors VahterBanBot.Tests/ContainerTestBase.fs's
/// MlEnabledVahterTestContainers (see AGENTS.md-adjacent binding note in the PR): booting TWO
/// instances against an empty DB exercises the advisory-lock cold-start path (lock id 1337 — one
/// instance trains, the other polls). Preloading the same pinned ml-model.bin the single-pod
/// suite commits means both instances normally find it already written and skip training; if the
/// file is absent (local dev without ever running VahterBanBot.Tests), both instances race
/// training for real, which is why AfterStart's /ready poll below is generous rather than
/// near-instant.
type VahterMultiPodContainers() =
    inherit MultiPodContainerBase(
        { Base =
            { BotProject = "VahterBanBot"
              MigrationsSubdir = "vahter-bot"
              DbName = "vahter_db"
              DbUser = "vahter_bot_ban_service"
              DbPassword = "vahter_bot_ban_service"
              AppImageName = "vahter-bot-multipod-test"
              OcrEnabled = false
              SecretToken = "OUR_SECRET"
              WebhookRoute = "/bot"
              AppEnvVars =
                [ "BOT_TELEGRAM_TOKEN", "123:456"
                  "BOT_AUTH_TOKEN", "OUR_SECRET"
                  "IGNORE_SIDE_EFFECTS", "false"
                  "USE_POLLING", "false"
                  "TELEGRAM_API_URL", "http://fake-tg-api:8080" ]
              PostgresImage = "postgres:17.10" }
          InstanceCount = 2 })

    // Same fixture file tests/VahterBanBot.Tests/ml-model.bin commits — not duplicated here.
    static let mlModelFixturePath =
        Path.Combine(CommonDirectoryPath.GetSolutionDirectory().DirectoryPath, "tests", "VahterBanBot.Tests", "ml-model.bin")

    static let isCi =
        let v = Environment.GetEnvironmentVariable "CI"
        not (String.IsNullOrEmpty v) && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v = "1")

    member _.ChatsToMonitor = Tg.chat(id = -666L, username = "pro.hell")
    member _.Vahter = Tg.user(id = 34L, username = "vahter_1")

    override this.SeedDatabase(connString: string) =
        task {
            use conn = new NpgsqlConnection(connString)
            do! conn.OpenAsync()
            let settings =
                [ "BOT_USER_ID", "1337", "FREE_FORM", "BOT"
                  "BOT_USER_NAME", "test_bot", "FREE_FORM", "BOT"
                  "POTENTIAL_SPAM_CHANNEL_ID", "-101", "FREE_FORM", "CHANNELS"
                  "DETECTED_SPAM_CHANNEL_ID", "-102", "FREE_FORM", "CHANNELS"
                  "ALL_LOGS_CHANNEL_ID", "-103", "FREE_FORM", "CHANNELS"
                  "CHATS_TO_MONITOR", """{"pro.hell":"-666"}""", "JSON_BLOB", "CHANNELS"
                  "ALLOWED_USERS", """{"vahter_1":"34"}""", "JSON_BLOB", "CHANNELS"
                  "ML_ENABLED", "true", "FEATURE_FLAG", "ML"
                  // Wall-clock daily retrain could otherwise rebuild the pinned model mid-suite —
                  // same rationale as VahterBanBot.Tests/ContainerTestBase.fs's mlSettings.
                  "ML_RETRAIN_SCHEDULED_ENABLED", "false", "FEATURE_FLAG", "ML"
                  // Needed for VahterMultiPodFeatureTests: ML_SPAM_DELETION_ENABLED makes a spam
                  // verdict (ML or spam-text-cache Enforce) actually DeleteSpam instead of just
                  // reporting, and SPAM_TEXT_CACHE_MODE=enforce activates the cross-pod cache path.
                  "ML_SPAM_DELETION_ENABLED", "true", "FEATURE_FLAG", "ML_SPAM_DELETION"
                  "SPAM_TEXT_CACHE_MODE", "enforce", "FREE_FORM", "SPAM_TEXT_CACHE"
                  // Short interval so the chat-admin convergence test doesn't wait on the
                  // production default (5 minutes) — see VahterMultiPodFeatureTests.fs.
                  "UPDATE_CHAT_ADMINS", "true", "FEATURE_FLAG", "CLEANUP"
                  "UPDATE_CHAT_ADMINS_INTERVAL_SEC", "3", "FREE_FORM", "CLEANUP" ]
            for (key, value, typ, group) in settings do
                let! _ =
                    conn.ExecuteAsync(
                        "INSERT INTO bot_setting(key,value,type,feature_group) VALUES(@k,@v,@t,@g) \
                         ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, type = EXCLUDED.type, feature_group = EXCLUDED.feature_group",
                        {| k = key; v = value; t = typ; g = group |})
                ()

            if File.Exists mlModelFixturePath then
                let! bytes = File.ReadAllBytesAsync mlModelFixturePath
                let! _ =
                    conn.ExecuteAsync(
                        "INSERT INTO ml_trained_model(id, model_data, created_at) VALUES (1, @data, now()) \
                         ON CONFLICT (id) DO UPDATE SET model_data = EXCLUDED.model_data, created_at = EXCLUDED.created_at",
                        {| data = bytes |})
                ()
            elif isCi then
                failwith $"ML model fixture missing at {mlModelFixturePath} — expected VahterBanBot.Tests/ml-model.bin to be committed."
        } :> Task

    override this.AfterStart() =
        task {
            let pollInterval = TimeSpan.FromMilliseconds 500.0
            let deadline = DateTime.UtcNow.Add(TimeSpan.FromMinutes 3.0)
            for i in 0 .. this.InstanceCount - 1 do
                let http = this.BotHttpAt(i)
                let mutable ready = false
                while not ready && DateTime.UtcNow < deadline do
                    try
                        let! resp = http.GetAsync("/ready")
                        if resp.IsSuccessStatusCode then
                            ready <- true
                        else
                            do! Task.Delay pollInterval
                    with _ ->
                        do! Task.Delay pollInterval
                if not ready then
                    failwith $"Vahter multi-pod instance {i} /ready did not return 200 within the deadline"
        } :> Task
