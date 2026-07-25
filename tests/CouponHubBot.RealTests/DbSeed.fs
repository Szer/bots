/// Seeds bot_setting into the transient real-test Postgres (COUPON_TEST_DB_URL) — the
/// CouponHubBot.RealTests analogue of AlitaBot.RealTests/DevDb.fs's
/// applyRealSettingsAsync. Unlike Alita's local-only DevDb (which also brings up
/// docker compose), this suite never starts Postgres itself: in CI the transient
/// in-cluster instance and its Flyway migrations are created by the workflow's k8s
/// manifests (out of this project's scope — see .github/k8s/coupon-test/*); locally,
/// the developer's own src/coupon-hub-bot/docker-compose.dev.yml plays that role. This
/// module only applies/refreshes rows in an already-reachable, already-migrated database.
module CouponHubBot.RealTests.DbSeed

open System.Threading.Tasks
open Dapper
open Npgsql

let private upsertSql =
    """
INSERT INTO bot_setting (key, value, type, feature_group, description)
VALUES (@key, @value, @typ, @grp, 'set by CouponHubBot.RealTests harness')
ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = NOW();
"""

/// The contract's "bot_setting seed for the transient DB" table, applied via UPSERT
/// (safe to call repeatedly / re-run). `communityChatId` is COUPON_TEST_CHAT_ID
/// (RealEnv.TestChatId); `feedbackAdminId` is the logged-in MTProto test user's own id
/// (contract: "the test MTProto user's own id" — only known after TgUserClient.LoginAsync,
/// so this can't be a static RealEnv field like the others).
///
/// IMPORTANT — TEST_MODE / FakeTimeProvider startup-ordering gotcha (Program.fs:103-121):
/// CouponHubBot registers FakeTimeProvider exactly ONCE, at process startup, gated on the
/// FIRST buildBotConf() read of bot_setting.TEST_MODE (a DB-only key with no env
/// fallback — see AGENTS.md). POST /reload-settings (RealAssemblyFixture.reloadSettingsAsync)
/// refreshes IOptions<BotConfiguration> live for every OTHER key below (COMMUNITY_CHAT_ID,
/// FEEDBACK_ADMINS, OCR_ENABLED, MAX_TAKEN_COUPONS, REMINDER_*, BATCH_DEBOUNCE_MS), but it
/// does NOT retroactively wire up FakeTimeProvider — that's a startup-only side effect. If
/// bot_setting is still empty (or TEST_MODE=false) the FIRST time the coupon-hub-bot
/// container process boots, POST /test/clock/advance 400s "FakeTimeProvider not registered"
/// for that pod's ENTIRE lifetime, and the bulk/album-add real test (coverage item 3 — driven
/// by BatchDebounce's TimeProvider-scheduled timer, see src/CouponHubBot/Services/BatchDebounce.fs)
/// can never finalize. `applyAsync` below MUST land BEFORE the bot process's own startup —
/// the same ordering src/coupon-hub-bot/docker-compose.dev.yml already encodes for local dev
/// (`seed-bot-settings` -> `bot`, via `depends_on: condition: service_completed_successfully`).
/// In CI this is the k8s manifests' responsibility (out of this project's scope); calling
/// `applyAsync` from RealAssemblyFixture is a best-effort refresh for the live-reloadable
/// keys, NOT a substitute for that pod-startup ordering — flagged prominently for whoever
/// wires up the coupon-test namespace.
let applyAsync (connectionString: string) (communityChatId: int64) (feedbackAdminId: int64) : Task =
    task {
        use conn = new NpgsqlConnection(connectionString)
        do! conn.OpenAsync()

        let settings =
            [ "TEST_MODE", "true", "FEATURE_FLAG", "CORE"
              "COMMUNITY_CHAT_ID", string communityChatId, "FREE_FORM", "CORE"
              "FEEDBACK_ADMINS", string feedbackAdminId, "FREE_FORM", "CORE"
              "OCR_ENABLED", "true", "FEATURE_FLAG", "OCR"
              "MAX_TAKEN_COUPONS", "6", "FREE_FORM", "CORE"
              "REMINDER_RUN_ON_START", "false", "FEATURE_FLAG", "REMINDER"
              "REMINDER_HOUR_DUBLIN", "10", "FREE_FORM", "REMINDER"
              "BATCH_DEBOUNCE_MS", "1000", "FREE_FORM", "BATCH" ]

        for key, value, typ, grp in settings do
            let! _ = conn.ExecuteAsync(upsertSql, {| key = key; value = value; typ = typ; grp = grp |})
            ()
    }

/// Admin-connection TRUNCATE helpers, modelled on
/// tests/CouponHubBot.Tests/ContainerTestBase.fs's TruncateCoupons/TruncateBatches —
/// the real suite owns its transient DB, so truncating between tests is legitimate
/// (contract, "Isolation model"). Real-Telegram round trips are slow (seconds per
/// poll), so real tests should truncate sparingly — only where a prior test's leftover
/// coupon rows would otherwise collide with a global query (e.g. "latest coupon id") —
/// unlike the hermetic suite, which truncates before nearly every test.
let truncateCouponsAsync (connectionString: string) : Task =
    task {
        use conn = new NpgsqlConnection(connectionString)
        do! conn.OpenAsync()
        let! _ = conn.ExecuteAsync "TRUNCATE coupon CASCADE"
        ()
    }

let truncateBatchesAsync (connectionString: string) : Task =
    task {
        use conn = new NpgsqlConnection(connectionString)
        do! conn.OpenAsync()
        let! _ = conn.ExecuteAsync "TRUNCATE pending_add_batch CASCADE"
        ()
    }

/// Current value of a bot_setting row — used by MembershipGateRealTests to save/restore
/// COMMUNITY_CHAT_ID around its DB-driven "point the gate at a chat this user isn't in" trick.
let getSettingAsync (connectionString: string) (key: string) : Task<string> =
    task {
        use conn = new NpgsqlConnection(connectionString)
        do! conn.OpenAsync()
        return! conn.QuerySingleAsync<string>("SELECT value FROM bot_setting WHERE key = @key", {| key = key |})
    }

let setSettingAsync (connectionString: string) (key: string) (value: string) : Task =
    task {
        use conn = new NpgsqlConnection(connectionString)
        do! conn.OpenAsync()
        let! _ =
            conn.ExecuteAsync(
                "UPDATE bot_setting SET value = @value, updated_at = NOW() WHERE key = @key",
                {| key = key; value = value |})
        ()
    }
