namespace AlitaBot.RealTests

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks

/// Ensures an ngrok tunnel https://{domain} -> http://127.0.0.1:5010 is up.
/// Reuses an already-running agent (queried via the local agent API on :4040);
/// otherwise spawns `ngrok http 5010 --url=https://{domain}` and polls until live.
/// Only a tunnel we spawned ourselves is killed on dispose.
type NgrokTunnel private (spawned: Process option, logWriter: TextWriter option, publicUrl: string) =

    static let agentTunnelsApi = "http://127.0.0.1:4040/api/tunnels"
    static let localPort = 5010
    static let startupTimeout = TimeSpan.FromSeconds 30.

    /// Local copy downloaded by the harness setup (ngrok is not on PATH on this host).
    static let ngrokBinary =
        let local = Path.Combine(RealEnv.alitaTestDir, "bin", "ngrok")
        if File.Exists local then local else "ngrok"

    /// Some(publicUrl) if the running agent already fronts our port/domain.
    static let tryFindTunnel (http: HttpClient) (domain: string) =
        task {
            try
                let! json = http.GetStringAsync agentTunnelsApi
                use doc = JsonDocument.Parse json

                let matches (t: JsonElement) =
                    let addr =
                        match t.TryGetProperty "config" with
                        | true, cfg ->
                            match cfg.TryGetProperty "addr" with
                            | true, a -> string a
                            | _ -> ""
                        | _ -> ""

                    let url =
                        match t.TryGetProperty "public_url" with
                        | true, u -> string u
                        | _ -> ""

                    addr.EndsWith $":{localPort}" || url.Contains domain

                return
                    doc.RootElement.GetProperty("tunnels").EnumerateArray()
                    |> Seq.tryFind matches
                    |> Option.map (fun t -> string (t.GetProperty "public_url"))
            with
            | :? HttpRequestException -> return None
            | :? TaskCanceledException -> return None
        }

    member _.PublicUrl = publicUrl

    static member ConnectAsync(domain: string, authtoken: string) =
        task {
            use http = new HttpClient(Timeout = TimeSpan.FromSeconds 3.)

            match! tryFindTunnel http domain with
            | Some url -> return NgrokTunnel(None, None, url)
            | None ->
                let logPath = Path.Combine(RealEnv.artifactsDir, "ngrok.log")

                let psi =
                    ProcessStartInfo(
                        FileName = ngrokBinary,
                        Arguments = $"http {localPort} --url=https://{domain} --log=stdout --log-format=json",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true)

                if authtoken <> "" then
                    psi.Environment["NGROK_AUTHTOKEN"] <- authtoken

                let log = TextWriter.Synchronized(new StreamWriter(logPath, append = false, AutoFlush = true))
                let proc = new Process(StartInfo = psi)
                proc.OutputDataReceived.Add(fun e -> if not (isNull e.Data) then log.WriteLine e.Data)
                proc.ErrorDataReceived.Add(fun e -> if not (isNull e.Data) then log.WriteLine e.Data)

                if not (proc.Start()) then
                    failwith $"Failed to start ngrok ({ngrokBinary})"

                proc.BeginOutputReadLine()
                proc.BeginErrorReadLine()

                let deadline = DateTime.UtcNow + startupTimeout
                let mutable url = None

                while url.IsNone && DateTime.UtcNow < deadline && not proc.HasExited do
                    do! Task.Delay 500
                    let! found = tryFindTunnel http domain
                    url <- found

                match url with
                | Some u -> return NgrokTunnel(Some proc, Some log, u)
                | None ->
                    try
                        proc.Kill(entireProcessTree = true)
                    with _ ->
                        ()

                    log.Dispose()

                    return
                        failwith
                            $"ngrok tunnel for https://{domain} did not come up within {startupTimeout.TotalSeconds}s — see {logPath}"
        }

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            match spawned with
            | Some proc ->
                task {
                    try
                        proc.Kill(entireProcessTree = true)
                        do! proc.WaitForExitAsync()
                    with _ ->
                        ()

                    proc.Dispose()
                    logWriter |> Option.iter (fun w -> w.Dispose())
                }
                |> ValueTask
            | None -> ValueTask.CompletedTask
