namespace AlitaBot

open System.Diagnostics
open System.Diagnostics.Metrics

/// ActivitySource for custom spans in traces (OTEL). Used by AddOpenTelemetry in Program.
module Telemetry =
    let botActivity = new ActivitySource("AlitaBot")

module Metrics =
    let meter = new Meter("AlitaBot.Metrics")

    /// Count of processed messages, tagged by `outcome` ∈ {logged, replied, ignored}.
    let messagesTotal = meter.CreateCounter<int64>("alitabot_messages_total")
