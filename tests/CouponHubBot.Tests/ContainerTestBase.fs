namespace CouponHubBot.Tests

open System
open System.Globalization
open System.Threading.Tasks
open BotTestInfra
open Dapper
open Npgsql
open Xunit

module private CouponTestConfig =
    let secret = "OUR_SECRET"
    let communityChatId = -42L
    let fixedUtcNow = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal)
    let fakeAzureAlias = "fake-azure-ocr"

    let makeConfig (ocrEnabled: bool) : BotContainerConfig =
        { BotProject = "CouponHubBot"
          MigrationsSubdir = "coupon-hub-bot"
          DbName = "coupon_hub_bot"
          DbUser = "coupon_hub_bot_service"
          DbPassword = "coupon_hub_bot_service"
          AppImageName = "coupon-hub-bot-test"
          OcrEnabled = ocrEnabled
          SecretToken = secret
          WebhookRoute = "/bot"
          AppEnvVars = [
              "BOT_TELEGRAM_TOKEN", "123:456"
              "BOT_AUTH_TOKEN", secret
              "TELEGRAM_API_URL", "http://fake-tg-api:8080"
              "GITHUB_TOKEN", ""
              "AZURE_OCR_KEY", (if ocrEnabled then "fake-key" else "")
              "BOT_FIXED_UTC_NOW", fixedUtcNow.ToString("o")
          ]
          FakeAiProjectOverride = None }

[<AbstractClass>]
type CouponHubTestContainers(seedExpiringToday: bool, ocrEnabled: bool) =
    inherit BotContainerBase(CouponTestConfig.makeConfig ocrEnabled)

    let mutable adminConnectionString: string = null

    let fixedDate = CouponTestConfig.fixedUtcNow.UtcDateTime.Date
    let fixedDateIso = fixedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

    override this.SeedDatabase(connString: string) =
        task {
            // Store admin connection string for truncate operations
            let parts = connString.Split("Port=")
            let port = parts.[1].Split(";").[0]
            adminConnectionString <- $"Server=127.0.0.1;Database=coupon_hub_bot;Port={port};User Id=admin;Password=admin;Include Error Detail=true;Timeout=120;Command Timeout=120;Keepalive=30;"

            use conn = new NpgsqlConnection(connString)
            do! conn.OpenAsync()

            // Seed bot_setting table
            let settings = [
                "COMMUNITY_CHAT_ID",      string CouponTestConfig.communityChatId, "FREE_FORM", "CORE"
                "REMINDER_HOUR_DUBLIN",    "10",                   "FREE_FORM", "REMINDER"
                "REMINDER_RUN_ON_START",   "false",                "FEATURE_FLAG", "REMINDER"
                "OCR_ENABLED",             (if ocrEnabled then "true" else "false"), "FEATURE_FLAG", "OCR"
                "OCR_MAX_FILE_SIZE_BYTES", "52428800",             "FREE_FORM", "OCR"
                "AZURE_OCR_ENDPOINT",      (if ocrEnabled then $"http://{CouponTestConfig.fakeAzureAlias}:8081" else ""), "FREE_FORM", "OCR"
                "FEEDBACK_ADMINS",         "900,901",              "FREE_FORM", "CORE"
                "GITHUB_REPO",             "",                     "FREE_FORM", "CORE"
                "TEST_MODE",               "true",                 "FEATURE_FLAG", "CORE"
                "MAX_TAKEN_COUPONS",       "4",                    "FREE_FORM", "CORE"
            ]
            for (key, value, typ, group) in settings do
                do! conn.ExecuteAsync(
                        "INSERT INTO bot_setting(key,value,type,feature_group) VALUES(@k,@v,@t,@g)",
                        {| k = key; v = value; t = typ; g = group |})
                    :> Task

            // Seed owner + expiring coupon before bot starts so ReminderRunOnStart can pick it up.
            if seedExpiringToday then
                //language=postgresql
                let userSql =
                    """
INSERT INTO "user"(id, username, first_name, created_at, updated_at)
VALUES (100, 'owner', 'Owner', NOW(), NOW())
ON CONFLICT (id) DO NOTHING;
"""
                let! _ = conn.ExecuteAsync(userSql)
                ()

                //language=postgresql
                let couponSql =
                    """
INSERT INTO coupon(owner_id, photo_file_id, value, min_check, expires_at, status)
VALUES (100, 'seed-photo', 10.00, 50.00, @expires_at::date, 'available');
"""
                let! _ = conn.ExecuteAsync(couponSql, {| expires_at = fixedDateIso |})
                ()
        }

    member _.CommunityChatId = CouponTestConfig.communityChatId
    member this.Bot = this.BotHttp
    member this.TelegramApi = this.FakeTgHttp
    member _.FixedUtcNow = CouponTestConfig.fixedUtcNow
    member _.FixedToday = DateOnly.FromDateTime(CouponTestConfig.fixedUtcNow.UtcDateTime)

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

    member _.TruncateCoupons() =
        task {
            use conn = new NpgsqlConnection(adminConnectionString)
            do! conn.OpenAsync()
            do! conn.ExecuteAsync("TRUNCATE coupon CASCADE") :> Task
        }

    member _.TruncateUserFeedback() =
        task {
            use conn = new NpgsqlConnection(adminConnectionString)
            do! conn.OpenAsync()
            do! conn.ExecuteAsync("TRUNCATE user_feedback CASCADE") :> Task
        }

type DefaultCouponHubTestContainers() =
    inherit CouponHubTestContainers(seedExpiringToday = false, ocrEnabled = false)

type OcrCouponHubTestContainers() =
    inherit CouponHubTestContainers(seedExpiringToday = false, ocrEnabled = true)
