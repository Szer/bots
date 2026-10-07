namespace Fizruk

open System
open System.Globalization
open System.Net.Http
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks

/// Factorio's current research over RCON. The command and parsing are pure so
/// they're testable without a server; `fetch` does the network call.
module FactorioResearch =

    type Research = { Name: string; Progress: float }

    /// Silent command, so players' in-game consoles don't echo it on every /status.
    /// Prints "<technology name>\t<level>\t<progress 0..1>", or "none" when idle.
    let command =
        "/sc local f = game.forces.player local r = f.current_research "
        + "if r then rcon.print(r.name .. '\\t' .. r.level .. '\\t' .. f.research_progress) "
        + "else rcon.print('none') end"

    let private levelSuffix = Regex(@"^(.*)-(\d+)$", RegexOptions.Compiled)

    /// "mining-productivity-3" at level 12 -> "Mining productivity 12";
    /// "steel-processing" -> "Steel processing".
    let displayName (technology: string) (level: int) : string =
        let m = levelSuffix.Match technology
        let baseName = if m.Success then m.Groups[1].Value else technology
        let words = baseName.Replace('-', ' ')
        let capitalized =
            if words = "" then words else string (Char.ToUpperInvariant words[0]) + words.Substring 1
        if m.Success || level > 1 then $"{capitalized} {level}" else capitalized

    let parse (body: string) : Result<Research option, string> =
        match body.Trim() with
        | "none" -> Ok None
        | "" -> Error "empty response"
        | text ->
            match text.Split('\t') with
            | [| name; level; progress |] ->
                match Int32.TryParse level, Double.TryParse(progress, NumberStyles.Float, CultureInfo.InvariantCulture) with
                | (true, lvl), (true, p) -> Ok(Some { Name = displayName name lvl; Progress = p })
                | _ -> Error $"unexpected research output '{text}'"
            | _ -> Error $"unexpected research output '{text}'"

    let format (result: Result<Research option, string>) : string =
        match result with
        | Ok None -> "Research: nothing queued."
        | Ok(Some r) ->
            let percent = (r.Progress * 100.0).ToString("0.0", CultureInfo.InvariantCulture)
            $"Research: {r.Name} ({percent}%%)."
        | Error e -> $"Research: unknown ({e})."

    let fetch (game: GameConfig) (timeout: TimeSpan) : Task<string> =
        task {
            match Rcon.passwordFor game.Players with
            | Error e -> return format (Error e)
            | Ok password ->
                let exec () = Rcon.execAsync game.Players.Host game.Players.Port password command timeout
                let! first = exec ()
                // A save that still has achievements enabled silently swallows its first
                // Lua command (empty output) and runs it only when repeated.
                let! result =
                    match first with
                    | Ok body when String.IsNullOrWhiteSpace body -> exec ()
                    | other -> Task.FromResult other
                return format (result |> Result.bind parse)
        }

module FactorioUps =

    let private http = new HttpClient()

    let private parse (body: string) : float option =
        try
            use doc = JsonDocument.Parse body
            let root = doc.RootElement
            if root.GetProperty("status").GetString() <> "success" then None
            else
                let data = root.GetProperty "data"
                let result = data.GetProperty "result"
                if data.GetProperty("resultType").GetString() <> "vector" || result.GetArrayLength() <> 1 then None
                else
                    let sample = result[0].GetProperty "value"
                    let value = sample[1].GetString()
                    match Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture) with
                    | true, ups when Double.IsFinite ups && ups >= 0.0 -> Some ups
                    | _ -> None
        with
        | :? JsonException | :? Collections.Generic.KeyNotFoundException
        | :? InvalidOperationException | :? IndexOutOfRangeException -> None

    let fetch (config: FactorioUpsConfig) (timeout: TimeSpan) : Task<string> =
        task {
            let! value =
                task {
                    try
                        // Require a recent active measurement so a paused or disconnected
                        // server cannot report its last known UPS as current performance.
                        let selector = config.MetricSelector
                        let query = $"avg(avg_over_time({selector}[5m]) and (time() - timestamp({selector}) < 30))"
                        let url = config.PrometheusUrl.AbsoluteUri.TrimEnd('/') + "/api/v1/query?query=" + Uri.EscapeDataString query
                        use cancellation = new CancellationTokenSource(timeout)
                        let! body = http.GetStringAsync(url, cancellation.Token)
                        return parse body
                    with
                    | :? HttpRequestException | :? OperationCanceledException -> return None
                }
            let result =
                match value with
                | Some ups -> ups.ToString("0.0", CultureInfo.InvariantCulture)
                | None -> "unavailable"
            return $"Avg UPS (last 5m, active): {result}."
        }

/// A running game's configured `details` lines, in config order. Fetchers render
/// their own failures as "unknown", so one broken detail never hides /status.
module StatusDetails =

    let private timeout = TimeSpan.FromSeconds 5.0

    let fetchLine (game: GameConfig) (detail: StatusDetail) : Task<string> =
        match detail with
        | StatusDetail.FactorioResearch -> FactorioResearch.fetch game timeout
        | StatusDetail.FactorioUps config -> FactorioUps.fetch config timeout

    let fetchAll (game: GameConfig) : Task<string list> =
        task {
            let! lines = game.Details |> List.map (fetchLine game) |> Task.WhenAll
            return List.ofArray lines
        }
