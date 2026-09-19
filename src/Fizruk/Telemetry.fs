namespace Fizruk

open System.Diagnostics
open System.Diagnostics.Metrics

/// ActivitySource for custom spans in traces (OTEL). Used by AddOpenTelemetry in Program.
module Telemetry =
    let botActivity = new ActivitySource("Fizruk")

module Metrics =
    let meter = new Meter("Fizruk.Metrics")

    /// Count of /start, /stop, /status invocations, tagged by `action` and `game`.
    let commandTotal = meter.CreateCounter<int64>("fizruk_command_total")

    /// Count of automatic idle shutdowns, tagged by `game`.
    let idleStopTotal = meter.CreateCounter<int64>("fizruk_idle_stop_total")
