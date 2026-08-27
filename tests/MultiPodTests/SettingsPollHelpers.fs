namespace MultiPodTests

open System
open System.Text.Json
open System.Threading.Tasks

/// Shared polling helper for the LISTEN/NOTIFY settings-propagation tests — not shared via a
/// project reference for the same reason as FakeCallHelpers.fs.
module SettingsPollHelpers =
    let private fieldAsString (root: JsonElement) (field: string) : string option =
        match root.TryGetProperty field with
        | true, v ->
            match v.ValueKind with
            | JsonValueKind.String -> Some(v.GetString())
            | JsonValueKind.True | JsonValueKind.False -> Some(string (v.GetBoolean()))
            | JsonValueKind.Number -> Some(v.GetRawText())
            | _ -> Some(v.GetRawText())
        | false, _ -> None

    /// Polls `dump()` every 250ms until `field` equals `expected` or `boundSeconds` elapse.
    /// Returns (reached, lastDumpSeen) so callers can print both dumps on timeout, not fail blind.
    let waitForFieldWithin (boundSeconds: float) (dump: unit -> Task<string>) (field: string) (expected: string) : Task<bool * string> =
        task {
            let deadline = DateTime.UtcNow.AddSeconds boundSeconds
            let mutable last = ""
            let mutable reached = false
            while not reached && DateTime.UtcNow < deadline do
                let! json = dump()
                last <- json
                reached <-
                    use doc = JsonDocument.Parse json
                    fieldAsString doc.RootElement field = Some expected
                if not reached then
                    do! Task.Delay 250
            return reached, last
        }

    /// 5s bound — cross-pod settings propagation via Postgres LISTEN/NOTIFY without a
    /// reconnect in play.
    let waitForField (dump: unit -> Task<string>) (field: string) (expected: string) : Task<bool * string> =
        waitForFieldWithin 5.0 dump field expected
