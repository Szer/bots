namespace CouponHubBot.Services

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open BotInfra
open CouponHubBot.Telemetry

/// Per-batch debounce timer for the album upload flow. Re-arming an existing
/// batch cancels the previous timer and starts fresh — this is how we "wait
/// for silence after the last album photo". Finalize is DB-driven, so we do
/// NOT track in-flight OCR tasks here.
///
/// The delay is scheduled through the injected `TimeProvider`. In production
/// this is `TimeProvider.System` (real wall-clock); in tests it is a
/// `FakeTimeProvider`, so tests can fire the debounce deterministically via
/// the `/test/clock/advance` endpoint instead of `Task.Delay`-ing in real time.
type BatchDebounce(logger: ILogger<BatchDebounce>, time: TimeProvider) =
    let pending = ConcurrentDictionary<int64, CancellationTokenSource>()

    member _.Schedule(batchId: int64, debounceMs: int, work: Func<Task>) : unit =
        let scheduledAt = DateTimeOffset.UtcNow
        let parentContext =
            if isNull Activity.Current then ActivityContext()
            else Activity.Current.Context
        let cts = new CancellationTokenSource()
        match pending.TryGetValue batchId with
        | true, existing ->
            try existing.Cancel() with _ -> ()
            existing.Dispose()
        | _ -> ()
        pending[batchId] <- cts
        let runLoop () : Task =
            task {
                let! elapsed = task {
                    use wait = botActivity.StartActivity("batchDebounce.wait", ActivityKind.Internal, parentContext, startTime = scheduledAt)
                    if not (isNull wait) then
                        %wait.SetTag("batchId", batchId)
                        %wait.SetTag("debounce.delay_ms", debounceMs)
                    try
                        do! Task.Delay(TimeSpan.FromMilliseconds(float debounceMs), time, cts.Token)
                        if not (isNull wait) then %wait.SetTag("debounce.outcome", "elapsed")
                        return true
                    with :? OperationCanceledException ->
                        if not (isNull wait) then %wait.SetTag("debounce.outcome", "superseded")
                        return false
                }
                if elapsed then
                    pending.TryRemove batchId |> ignore
                    try
                        do! work.Invoke()
                    with ex ->
                        logger.LogError(ex, "FinalizeBatch failed for batch {BatchId}", batchId)
            } :> Task
        Task.Run<Task>(Func<Task>(runLoop)) |> ignore
