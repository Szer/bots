namespace CouponHubBot.Services

open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open CouponHubBot

/// At startup, scan for batches in `status='open'` (the bot crashed while OCR
/// was in flight, or before finalize ran). For each:
///   - re-fire OCR in the background for any `pending` items (idempotent —
///     OCR's UPDATE is conditional on `status='pending'`).
///   - re-arm the debounce timer so finalize fires ~`debounceMs` after recovery.
/// `awaiting_user` batches are deliberately skipped: their bulk-confirm
/// message is already in the user's chat and callbacks read state from DB.
type BatchRecoveryService(
    db: DbService,
    couponFlow: CouponFlowHandler,
    batchDebounce: BatchDebounce,
    options: IOptions<BotConfiguration>,
    logger: ILogger<BatchRecoveryService>
) =
    interface IHostedService with
        member _.StartAsync(_ct: CancellationToken) =
            task {
                try
                    let! batches = db.GetOpenBatchesForRecovery()
                    if batches.Length > 0 then
                        logger.LogInformation("Recovering {Count} incomplete album batch(es)", batches.Length)
                    for batch in batches do
                        let! items = db.GetBatchItems batch.id
                        for item in items do
                            if item.status = "pending" then
                                Task.Run(fun () ->
                                    couponFlow.OcrItem batch.id item.id item.photo_file_id)
                                |> ignore
                        batchDebounce.Schedule(
                            batch.id,
                            options.Value.BatchDebounceMs,
                            System.Func<Task>(fun () -> couponFlow.FinalizeBatch batch.id))
                with ex ->
                    logger.LogError(ex, "Batch recovery failed; incomplete batches will be reaped by TTL")
            } :> Task

        member _.StopAsync(_ct: CancellationToken) = Task.CompletedTask
