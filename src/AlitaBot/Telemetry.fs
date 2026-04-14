namespace AlitaBot

open System.Diagnostics
open System.Diagnostics.Metrics

module Telemetry =
    let botActivity = new ActivitySource("AlitaBot")

module Metrics =
    let meter = new Meter("AlitaBot.Metrics")

    /// Count of messages processed, tagged by `outcome` (replied, silent, emoji, chaos).
    let messagesProcessed = meter.CreateCounter<int64>("alitabot_messages_processed_total")

    /// Count of proactive posts sent.
    let proactivePostsTotal = meter.CreateCounter<int64>("alitabot_proactive_posts_total")

    /// Count of dossier updates performed.
    let dossierUpdatesTotal = meter.CreateCounter<int64>("alitabot_dossier_updates_total")
