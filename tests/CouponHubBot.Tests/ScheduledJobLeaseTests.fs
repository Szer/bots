namespace CouponHubBot.Tests

open System
open System.Threading.Tasks
open BotInfra
open Xunit

/// Multi-pod safety: `BotInfra.ScheduledJobs.tryAcquire`'s already-completed-today
/// no-op. The exactly-one-winner guarantee this file used to also cover is now
/// proven at API level by MultiPodTests' CouponMultiPodReminderLeaseTests (a real
/// two-pod race). This one case stays here because it isn't a race at all — a
/// single pod re-ticking a job already marked done today — so there's no
/// multi-pod API scenario that exercises it; it's only reachable directly
/// against `tryAcquire`.
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
