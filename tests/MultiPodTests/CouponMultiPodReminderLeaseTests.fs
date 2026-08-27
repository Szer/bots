namespace MultiPodTests

open System
open System.Diagnostics
open System.Threading.Tasks
open BotTestInfra
open MultiPodTests.FakeCallHelpers
open Npgsql
open Dapper
open Xunit

/// Proves ReminderService's multi-pod lease: TWO real instances race the SAME `reminder_daily`
/// row when their clocks cross the scheduled slot -- not RunJobNow, which bypasses the lease entirely.
type CouponMultiPodReminderLeaseTests(fixture: CouponMultiPodContainers) =

    [<Fact>]
    let ``Reminder lease: two instances ticking past the scheduled slot produce exactly one community post`` () = task {
        do! fixture.ClearFakeCalls()

        use conn = new NpgsqlConnection(fixture.DbConnectionString)
        do! conn.OpenAsync()

        // Matches Postgres's own unfaked CURRENT_DATE as long as the run doesn't straddle midnight UTC.
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

        // Small-step advance avoids racing SchedulerHostedService's PeriodicTimer construction at
        // startup (see ClockAdvanceHelpers). 150s bound: CI shares the runner with VahterBanBot's ML fixture.
        let expectedText = "Сегодня истекает 1 купон на сумму"
        let mutable matchCount = 0
        let! _ =
            ClockAdvanceHelpers.advanceUntil
                fixture.AdvanceAllClocks
                (fun () -> task {
                    let! calls = fixture.GetFakeCalls("sendMessage")
                    matchCount <- countCallsWithText calls fixture.CommunityChatId expectedText
                    return matchCount > 0
                })
                (90 * 1000)
                300
                150000
        Assert.True(matchCount > 0, $"Timeout: no reminder post matching '{expectedText}' after 150000ms")

        // Dedupe by content: if BOTH pods had won the lease, this would be 2.
        Assert.Equal(1, matchCount)

        // `complete` writes last_completed_at AFTER the send awaits, so it can still be in
        // flight the instant the sendMessage poll above matches -- poll here too, not a single read.
        let sw2 = Stopwatch.StartNew()
        let mutable completedAt = Nullable<DateTime>()
        while not completedAt.HasValue && sw2.ElapsedMilliseconds < 60000L do
            let! v =
                conn.QuerySingleOrDefaultAsync<Nullable<DateTime>>(
                    "SELECT last_completed_at FROM scheduled_job WHERE job_name = 'reminder_daily'")
            completedAt <- v
            if not completedAt.HasValue then do! Task.Delay 200
        Assert.True(completedAt.HasValue, "Expected reminder_daily.last_completed_at to be set after the tick")

        // Logs are raw Serilog JSON, so the {Job} template renders as a quoted "reminder_daily" --
        // match both substrings rather than the literal unquoted phrase.
        let! log0 = fixture.GetBotLogs(0)
        let! log1 = fixture.GetBotLogs(1)
        let acquiredCount =
            [ log0; log1 ]
            |> List.filter (fun l -> l.Contains "ScheduledJobs: acquired" && l.Contains "reminder_daily")
            |> List.length
        Assert.Equal(1, acquiredCount)
    }
