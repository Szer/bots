namespace CouponHubBot.Services

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging

/// Per-batch debounce timer for the album upload flow. Re-arming an existing
/// batch cancels the previous timer and starts fresh — this is how we "wait
/// for silence after the last album photo". Finalize is DB-driven, so we do
/// NOT track in-flight OCR tasks here.
type BatchDebounce(logger: ILogger<BatchDebounce>) =
    let pending = ConcurrentDictionary<int64, CancellationTokenSource>()

    member _.Schedule(batchId: int64, debounceMs: int, work: Func<Task>) : unit =
        let cts = new CancellationTokenSource()
        match pending.TryGetValue batchId with
        | true, existing ->
            try existing.Cancel() with _ -> ()
            existing.Dispose()
        | _ -> ()
        pending[batchId] <- cts
        let runLoop () : Task =
            task {
                try
                    do! Task.Delay(debounceMs, cts.Token)
                    pending.TryRemove batchId |> ignore
                    try
                        do! work.Invoke()
                    with ex ->
                        logger.LogError(ex, "FinalizeBatch failed for batch {BatchId}", batchId)
                with :? OperationCanceledException -> ()
            } :> Task
        Task.Run<Task>(Func<Task>(runLoop)) |> ignore
