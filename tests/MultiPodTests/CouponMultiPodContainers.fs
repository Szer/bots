namespace MultiPodTests

open System.Threading.Tasks
open BotTestInfra
open Npgsql
open Dapper

/// 2-instance CouponHubBot fixture. TEST_MODE gives EACH instance its own FakeTimeProvider
/// (per-process) — MultiPodContainerBase.AdvanceAllClocks is the only supported way to move
/// time on this fixture; advancing one instance's clock alone desyncs it from the others' when
/// cross-pod logic compares this-instance `now` against another instance's DB-persisted
/// timestamp (e.g. the batch debounce).
type CouponMultiPodContainers() =
    inherit MultiPodContainerBase(
        { Base =
            { BotProject = "CouponHubBot"
              MigrationsSubdir = "coupon-hub-bot"
              DbName = "coupon_hub_bot"
              DbUser = "coupon_hub_bot_service"
              DbPassword = "coupon_hub_bot_service"
              AppImageName = "coupon-hub-bot-multipod-test"
              OcrEnabled = false
              SecretToken = "OUR_SECRET"
              WebhookRoute = "/bot"
              AppEnvVars =
                [ "BOT_TELEGRAM_TOKEN", "123:456"
                  "BOT_AUTH_TOKEN", "OUR_SECRET"
                  "TELEGRAM_API_URL", "http://fake-tg-api:8080"
                  "GITHUB_TOKEN", "" ]
              PostgresImage = "postgres:17.10" }
          InstanceCount = 2 })

    member _.CommunityChatId = -42L

    override this.SeedDatabase(connString: string) =
        task {
            use conn = new NpgsqlConnection(connString)
            do! conn.OpenAsync()
            let settings =
                [ "COMMUNITY_CHAT_ID", string this.CommunityChatId, "FREE_FORM", "CORE"
                  "TEST_MODE", "true", "FEATURE_FLAG", "CORE"
                  "REMINDER_RUN_ON_START", "false", "FEATURE_FLAG", "REMINDER" ]
            for (key, value, typ, group) in settings do
                let! _ =
                    conn.ExecuteAsync(
                        "INSERT INTO bot_setting(key,value,type,feature_group) VALUES(@k,@v,@t,@g) \
                         ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, type = EXCLUDED.type, feature_group = EXCLUDED.feature_group",
                        {| k = key; v = value; t = typ; g = group |})
                ()
        } :> Task
