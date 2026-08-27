namespace MultiPodTests

open System
open System.Threading.Tasks

/// Robust clock-advance helper for cross-pod hosted-service tests. A single big
/// `AdvanceAllClocks` call races the app's own host startup: `Host.StartAsync` awaits each
/// `IHostedService.StartAsync` in registration order, but Kestrel (and this fixture's
/// port-open wait strategy) is satisfied by the FIRST one — so a later-registered service's
/// `PeriodicTimer` (e.g. `BotInfra.SchedulerHostedService`) can still be constructed AFTER a
/// single upfront advance has already landed. Since `PeriodicTimer(period, timeProvider)`
/// captures "now" at construction and schedules its first due-tick at `now + period`, a timer
/// built after the only advance never sees a later `now` and never fires. Advancing in small
/// steps with real-time yields between them means a timer constructed at ANY point during the
/// loop still accumulates a full tick period of advances afterward.
module ClockAdvanceHelpers =
    /// Repeatedly calls `advance stepMs` then sleeps `realSleepMs` of real time and checks
    /// `isDone`, until `isDone` returns true or `maxWaitMs` of real time elapses.
    let advanceUntil
        (advance: int -> Task<unit>)
        (isDone: unit -> Task<bool>)
        (stepMs: int)
        (realSleepMs: int)
        (maxWaitMs: int)
        : Task<bool> =
        task {
            let deadline = DateTime.UtcNow.AddMilliseconds(float maxWaitMs)
            let mutable case = false
            while not case && DateTime.UtcNow < deadline do
                do! advance stepMs
                do! Task.Delay realSleepMs
                let! d = isDone()
                case <- d
            return case
        }
