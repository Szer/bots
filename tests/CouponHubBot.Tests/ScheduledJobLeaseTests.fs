namespace CouponHubBot.Tests

open System
open System.Threading.Tasks
open BotInfra
open Xunit

/// `tryAcquire`'s already-completed-today no-op: a single pod re-ticking a job already done
/// today, not a race -- only reachable by calling `tryAcquire` directly.
type ScheduledJobLeaseTests(fixture: DefaultCouponHubTestContainers) =

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
