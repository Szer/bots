namespace CouponHubBot

open System.Diagnostics
open System.Diagnostics.Metrics

/// ActivitySource for custom spans in traces (OTEL). Used by AddOpenTelemetry in Program.
module Telemetry =
    let botActivity = new ActivitySource("CouponHubBot")

module Metrics =
    let meter = new Meter("CouponHubBot.Metrics")

    /// Count of UI/button interactions, tagged by `button`.
    let buttonClickTotal = meter.CreateCounter<int64>("couponhubbot_button_click_total")

    /// Count of command invocations, tagged by `command` (e.g. "list", "add", "feedback").
    let commandTotal = meter.CreateCounter<int64>("couponhubbot_command_total")

    /// Count of callback query actions, tagged by `action` (e.g. "take", "return", "used", "void", "addflow", "myAdded").
    let callbackTotal = meter.CreateCounter<int64>("couponhubbot_callback_total")

    /// Count of user feedback submissions via /feedback flow.
    let feedbackTotal = meter.CreateCounter<int64>("couponhubbot_feedback_total")

    // ── Album batch flow ──────────────────────────────────────────────
    // The data the original feature was justified on (size-of-burst, OCR
    // success rate) lived in DB queries. These metrics make the same
    // signals live in Prometheus so we can dashboard + alert on them.
    // Cardinality note: all tags below are bounded enums; no user/batch ids.

    /// 1 per new batch opened (first photo of an album, no existing open batch
    /// for that user+mediaGroupId).
    let batchCreatedTotal = meter.CreateCounter<int64>("couponhubbot_batch_created_total")

    /// 1 per batch item after OCR reaches a terminal state.
    /// Tags: outcome ∈ {ok, needs_input}, failure_note ∈ {no barcode, partial,
    /// timeout, OCR failed, OCR disabled} (empty for outcome=ok).
    let batchItemOutcomeTotal = meter.CreateCounter<int64>("couponhubbot_batch_item_outcome_total")

    /// 1 per FinalizeBatch invocation that wins TryFlipBatchToAwaiting.
    /// Tag: outcome ∈ {ok, fallback}. `fallback` means the render/send pipeline
    /// threw — alarmable.
    let batchFinalizedTotal = meter.CreateCounter<int64>("couponhubbot_batch_finalized_total")

    /// Items-per-batch at finalize time. Tag: outcome (same as batchFinalizedTotal).
    let batchSize = meter.CreateHistogram<int>("couponhubbot_batch_size")

    /// 1 per user click on the "Подтвердить N купонов" button.
    let batchConfirmTotal = meter.CreateCounter<int64>("couponhubbot_batch_confirm_total")

    /// Sum of newly-inserted coupons across confirm clicks (NOT the click count).
    /// Pair with batchConfirmTotal to derive average added-per-confirm.
    let batchAddedTotal = meter.CreateCounter<int64>("couponhubbot_batch_added_total")

    /// 1 per item that was skipped during confirm (duplicate, expired).
    /// Tag: reason ∈ {DuplicateBarcode, DuplicatePhoto, Expired}.
    let batchSkippedTotal = meter.CreateCounter<int64>("couponhubbot_batch_skipped_total")

    /// 1 per user click on the "Отменить" button on a bulk-confirm message.
    /// Tag: had_ok_items ∈ {true, false} — was the cancelled batch usable?
    let batchCancelTotal = meter.CreateCounter<int64>("couponhubbot_batch_cancel_total")

    /// 1 per active batch that was abandoned without the user clicking confirm/cancel.
    /// Tag: reason ∈ {supersede_album, command}.
    /// (TTL reaps are not yet counted here — would need a return value from
    /// CreateBatchAtomically's housekeeping DELETE.)
    let batchAbandonedTotal = meter.CreateCounter<int64>("couponhubbot_batch_abandoned_total")
