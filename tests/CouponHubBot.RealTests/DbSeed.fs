/// Seeds bot_setting into the transient real-test Postgres (COUPON_TEST_DB_URL) — the
/// CouponHubBot.RealTests analogue of AlitaBot.RealTests/DevDb.fs's
/// applyRealSettingsAsync. Unlike Alita's local-only DevDb (which also brings up
/// docker compose), this suite never starts Postgres itself: in CI the transient
/// in-cluster instance and its Flyway migrations are created by the workflow's k8s
/// manifests (out of this project's scope — see .github/k8s/coupon-test/*); locally,
/// the developer's own src/coupon-hub-bot/docker-compose.dev.yml plays that role. This
/// module only applies/refreshes rows in an already-reachable, already-migrated database.
module CouponHubBot.RealTests.DbSeed

open System
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

/// Status + holder columns for one coupon id — the shape every take/used/return/report
/// real test's final DB assertion needs. `owner_id`/`taken_by` are exposed too (rather
/// than just `status`) so a caller can assert who currently holds it without a second
/// query — `taken_by` is nullable in the schema (coupon.taken_by BIGINT NULL), hence
/// `Nullable<int64>` per the src/CouponHubBot/Services/DbService.fs convention for the
/// same column, not `int64 option` (AGENTS.md's Dapper-nullable-column rule technically
/// only calls out `string | null`, but `coupon.taken_by`'s existing F# mapping already
/// sets the precedent for this column specifically).
[<CLIMutable>]
type CouponStatusRow =
    { status: string
      owner_id: int64
      taken_by: Nullable<int64> }

/// Coupon status/owner/taken_by by id — used by every take/used/return/report/void real
/// test's terminal-state DB assertion (`SELECT status FROM coupon WHERE id=@id` is
/// currently copy-pasted inline in CouponLifecycleRealTests.fs and ReportFlowRealTests.fs;
/// this gives that query one home and adds owner_id/taken_by for the tests that need them).
let getCouponStatusAsync (connectionString: string) (couponId: int) : Task<CouponStatusRow> =
    task {
        use conn = new NpgsqlConnection(connectionString)
        do! conn.OpenAsync()
        return!
            conn.QuerySingleAsync<CouponStatusRow>(
                "SELECT status, owner_id, taken_by FROM coupon WHERE id = @coupon_id",
                {| coupon_id = couponId |})
    }

/// Newest coupon id owned by `ownerId` — the "SELECT id FROM coupon WHERE owner_id=@o
/// ORDER BY id DESC LIMIT 1" pattern copy-pasted inline in AddFlowRealTests.fs,
/// CouponLifecycleRealTests.fs and ReportFlowRealTests.fs, given one home. Relies on the
/// same single-account/no-cross-test-ordering assumptions those call sites already
/// document — call it immediately after the add that's supposed to have produced the
/// coupon, before any other add for the same owner can land.
let getLatestOwnedCouponIdAsync (connectionString: string) (ownerId: int64) : Task<int> =
    task {
        use conn = new NpgsqlConnection(connectionString)
        do! conn.OpenAsync()
        return!
            conn.QuerySingleAsync<int>(
                "SELECT id FROM coupon WHERE owner_id = @owner_id ORDER BY id DESC LIMIT 1",
                {| owner_id = ownerId |})
    }

/// Overwrites `expires_at` for one coupon id via direct SQL — BulkAddRealTests.fs's
/// `bumpItemsToFutureExpiry` already does the pending_add_batch_item equivalent of this
/// inline (it needs to override OCR's own past-dated expiry before TryAddCoupon's
/// `expires_at < todayUtc()` gate runs); the interactive wizard tests need the same trick
/// against the `coupon` table itself once a coupon already exists (e.g. to manufacture an
/// expiring-today/expired coupon for a status-dependent flow) rather than pending_add_batch_item.
let setCouponExpiryAsync (connectionString: string) (couponId: int) (expiresAt: DateOnly) : Task =
    task {
        use conn = new NpgsqlConnection(connectionString)
        do! conn.OpenAsync()
        let! _ =
            conn.ExecuteAsync(
                "UPDATE coupon SET expires_at = @expires_at WHERE id = @coupon_id",
                {| coupon_id = couponId; expires_at = expiresAt |})
        ()
    }

/// Deletes every coupon row for a given `barcode_text` (all expiries, all statuses) —
/// THE helper that makes /add fixture-reuse safe (contract, "problem" section 1+2).
/// `coupon_event.coupon_id REFERENCES coupon(id) ON DELETE CASCADE` (V1__initial.sql) is
/// the only FK pointing at `coupon` anywhere in migrations/ (checked: `grep -rl
/// "REFERENCES coupon"` finds only V1__initial.sql, which defines that one cascade) — no
/// extra child-table deletes are needed; a plain DELETE on `coupon` cascades automatically.
/// Call this BEFORE sending a fixture photo, not after: it's what makes a retried /add
/// attempt (TestRetry's one automatic retry, after an AwaitTimeoutException on attempt 1)
/// safe even if attempt 1's coupon actually landed — and what lets the same fixture image
/// be reused by many different tests instead of needing one distinct barcode per test.
let deleteCouponsByBarcodeAsync (connectionString: string) (barcodeText: string) : Task =
    task {
        use conn = new NpgsqlConnection(connectionString)
        do! conn.OpenAsync()
        let! _ = conn.ExecuteAsync("DELETE FROM coupon WHERE barcode_text = @barcode_text", {| barcode_text = barcodeText |})
        ()
    }

/// Clears a user's in-flight interactive `/add` wizard state (the `pending_add` table —
/// mapped in src/CouponHubBot/Services/DbService.fs as the `PendingAddFlow` record;
/// there is no separate "PendingAddFlow table", it's `pending_add`, checked against
/// V6__recreate_pending_add.sql and DbService.fs's own ClearPendingAddFlow: "DELETE FROM
/// pending_add WHERE user_id = @user_id"). Lets a wizard test guarantee a clean starting
/// stage regardless of what a previous test — or a previous TestRetry attempt of the SAME
/// test — left behind; GetPendingAddFlow's own 1-hour staleness expiry
/// (DbService.fs:758-770) is too coarse-grained to rely on between fast-running tests.
let deletePendingAddFlowAsync (connectionString: string) (userId: int64) : Task =
    task {
        use conn = new NpgsqlConnection(connectionString)
        do! conn.OpenAsync()
        let! _ = conn.ExecuteAsync("DELETE FROM pending_add WHERE user_id = @user_id", {| user_id = userId |})
        ()
    }

/// Clears a user's in-flight album/bulk-add batches (`pending_add_batch`, all statuses —
/// V16__pending_add_batch.sql). `pending_add_batch_item.batch_id REFERENCES
/// pending_add_batch(id) ON DELETE CASCADE`, so deleting the parent row is enough; no
/// separate pending_add_batch_item delete needed. Lets an album/bulk-add wizard test
/// guarantee no stale batch survives from a previous test or retry attempt — BulkAddRealTests
/// already relies on `pending_add_batch_user_mgid_active_uniq` (one active batch per
/// (user_id, media_group_id) while status IN ('open','awaiting_user')) never colliding
/// with a leftover row from an earlier run; this is that cleanup, given one home.
let deletePendingBatchesAsync (connectionString: string) (userId: int64) : Task =
    task {
        use conn = new NpgsqlConnection(connectionString)
        do! conn.OpenAsync()
        let! _ = conn.ExecuteAsync("DELETE FROM pending_add_batch WHERE user_id = @user_id", {| user_id = userId |})
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
