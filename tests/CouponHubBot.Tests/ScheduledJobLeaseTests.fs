namespace CouponHubBot.Tests

open System
open System.Threading.Tasks
open BotInfra
open Xunit

/// Multi-pod safety: `BotInfra.ScheduledJobs.tryAcquire` is the primitive that
/// guarantees exactly one pod runs a scheduled job per day (2+ CouponHubBot
/// pods behind the same `scheduled_job` row — seeded by
/// coupon-hub-bot/migrations/V19__scheduled_jobs.sql). Exercises the lease
/// directly against the fixture's own Postgres rather than through the HTTP
/// surface, since the lease itself is what needs proving here, not
/// ReminderService's business logic (covered by ReminderTests.fs).
type ScheduledJobLeaseTests(fixture: DefaultCouponHubTestContainers) =

    [<Fact>]
    let ``Two concurrent tryAcquire calls for the same due job: exactly one wins`` () =
        task {
            // Reset the seeded row so this test doesn't depend on run order.
            let! _ =
                fixture.Execute(
                    "UPDATE scheduled_job SET last_completed_at = NULL, locked_until = NULL, locked_by = NULL WHERE job_name = 'reminder_daily'",
                    null)

            let time = TimeProvider.System
            // TimeSpan.Zero = midnight today, so "@now >= CURRENT_DATE + scheduledTime"
            // is true at any time of day the test happens to run.
            let! results =
                Task.WhenAll(
                    ScheduledJobs.tryAcquire fixture.DbConnectionString time "reminder_daily" TimeSpan.Zero "pod-a",
                    ScheduledJobs.tryAcquire fixture.DbConnectionString time "reminder_daily" TimeSpan.Zero "pod-b")

            let winners = results |> Array.filter id
            Assert.Equal(1, winners.Length)

            // The loser must see a lease already held, not a second free acquire.
            let! lockedBy =
                fixture.QuerySingle<string>(
                    "SELECT locked_by FROM scheduled_job WHERE job_name = 'reminder_daily'", null)
            Assert.True(lockedBy = "pod-a" || lockedBy = "pod-b")
        }

    [<Fact>]
    let ``tryAcquire is a no-op once completed today`` () =
        task {
            let! _ =
                fixture.Execute(
                    "UPDATE scheduled_job SET last_completed_at = NOW(), locked_until = NULL, locked_by = NULL WHERE job_name = 'reminder_daily'",
                    null)

            let time = TimeProvider.System
            let! acquired = ScheduledJobs.tryAcquire fixture.DbConnectionString time "reminder_daily" TimeSpan.Zero "pod-c"
            Assert.False(acquired, "A job already completed today must not be re-acquired")
        }
