namespace MultiPodTests

open System
open System.Diagnostics
open System.Threading.Tasks
open BotTestInfra
open MultiPodTests.FakeCallHelpers
open Npgsql
open Dapper
open Xunit

/// Proves ReminderService's multi-pod lease at the process level: TWO real
/// CouponHubBot instances, each running their own BotInfra.SchedulerHostedService,
/// race the SAME `reminder_daily` row when their clocks cross the scheduled slot —
/// not `/test/run-reminder`'s RunJobNow, which bypasses the lease entirely and
/// would prove nothing about it.
///
/// REMINDER_HOUR_DUBLIN is left at its default (10, Dublin); determinism comes from
/// CouponMultiPodContainers pinning BOT_FIXED_UTC_NOW to noon UTC on today's real
/// date instead — safely past the scheduled UTC slot (09:00 summer / 10:00 winter)
/// regardless of what hour CI actually runs at.
type CouponMultiPodReminderLeaseTests(fixture: CouponMultiPodContainers) =

    [<Fact>]
    let ``Reminder lease: two instances ticking past the scheduled slot produce exactly one community post`` () = task {
        do! fixture.ClearFakeCalls()

        use conn = new NpgsqlConnection(fixture.DbConnectionString)
        do! conn.OpenAsync()

        // "Today" per the fixture's pinned BOT_FIXED_UTC_NOW (noon UTC on the real
        // date the fixture was built) — matches Postgres's own unfaked CURRENT_DATE
        // as long as the run doesn't straddle midnight UTC.
        let todayIso = fixture.FixedUtcNow.UtcDateTime.Date.ToString("yyyy-MM-dd")
        let! _ =
            conn.ExecuteAsync(
                """INSERT INTO "user"(id, username, first_name, created_at, updated_at)
                   VALUES (960001,'lease_owner','LeaseOwner',NOW(),NOW()) ON CONFLICT (id) DO NOTHING;""")
        let! _ =
            conn.ExecuteAsync(
                """INSERT INTO coupon(owner_id, photo_file_id, value, min_check, expires_at, status)
                   VALUES (960001,'lease-photo-1',10.00,50.00,@today::date,'available')
                   ON CONFLICT DO NOTHING;""",
                {| today = todayIso |})

        // Reset the lease row so this test doesn't depend on run order relative to
        // other tests sharing this fixture (e.g. an earlier /test/run-reminder call).
        let! _ =
            conn.ExecuteAsync(
                "UPDATE scheduled_job SET last_completed_at = NULL, locked_until = NULL, locked_by = NULL WHERE job_name = 'reminder_daily'")

        // Push BOTH instances' clocks past CouponScheduledJobs' 5-minute tick
        // interval (Program.fs's SchedulerHostedService registration) so both
        // SchedulerHostedServices actually tick and both attempt tryAcquire against
        // the same row — this is the natural production race, not a bypass.
        do! fixture.AdvanceAllClocks(6 * 60 * 1000)

        let expectedText = "Сегодня истекает 1 купон на сумму"
        let sw = Stopwatch.StartNew()
        let mutable matchCount = 0
        while matchCount = 0 && sw.ElapsedMilliseconds < 15000L do
            let! calls = fixture.GetFakeCalls("sendMessage")
            matchCount <- countCallsWithText calls fixture.CommunityChatId expectedText
            if matchCount = 0 then do! Task.Delay 200
        Assert.True(matchCount > 0, $"Timeout: no reminder post matching '{expectedText}' after 15000ms")

        // Dedupe by content: if BOTH pods had won the lease, this would be 2.
        Assert.Equal(1, matchCount)

        // DB evidence: the lease was completed (last_completed_at set from NULL).
        // `complete` writes this in the line AFTER the message send job.Run() awaits,
        // so it can still be in flight the instant the sendMessage poll above finds its
        // match — poll here too rather than reading once.
        let sw2 = Stopwatch.StartNew()
        let mutable completedAt = Nullable<DateTime>()
        while not completedAt.HasValue && sw2.ElapsedMilliseconds < 5000L do
            let! v =
                conn.QuerySingleOrDefaultAsync<Nullable<DateTime>>(
                    "SELECT last_completed_at FROM scheduled_job WHERE job_name = 'reminder_daily'")
            completedAt <- v
            if not completedAt.HasValue then do! Task.Delay 200
        Assert.True(completedAt.HasValue, "Expected reminder_daily.last_completed_at to be set after the tick")

        // Process-level evidence: exactly one instance's own log shows it won
        // tryAcquire (BotInfra.ScheduledJobs' "ScheduledJobs: acquired {Job}" line) —
        // the other instance's tick must have seen the lease already held/expired-not.
        // Logs are raw Serilog JSON (GetBotLogs dumps container stdout), so the message
        // template's {Job} renders as a quoted "reminder_daily" — match both substrings
        // rather than the literal unquoted phrase.
        let! log0 = fixture.GetBotLogs(0)
        let! log1 = fixture.GetBotLogs(1)
        let acquiredCount =
            [ log0; log1 ]
            |> List.filter (fun l -> l.Contains "ScheduledJobs: acquired" && l.Contains "reminder_daily")
            |> List.length
        Assert.Equal(1, acquiredCount)
    }
