namespace MultiPodTests

open System
open System.Threading.Tasks

/// A single big `AdvanceAllClocks` call races host startup: a `PeriodicTimer` constructed AFTER
/// it (Kestrel is ready before every `IHostedService` starts) captures a stale "now" and never fires.
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
