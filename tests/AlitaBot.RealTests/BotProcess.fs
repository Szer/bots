namespace AlitaBot.RealTests

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Threading.Tasks

/// Spawns the Release-built AlitaBot.dll as a child process on 127.0.0.1:5010,
/// streaming stdout/stderr to test-artifacts/AlitaBot.RealTests/bot.log,
/// and waits for /healthz. Killed on dispose.
type BotProcess private (proc: Process, log: TextWriter, logPath: string) =

    static let listenUrl = "http://127.0.0.1:5010"
    static let healthTimeout = TimeSpan.FromSeconds 60.

    static member DllPath =
        Path.Combine(RealEnv.repoRoot, "src", "AlitaBot", "bin", "Release", "net10.0", "AlitaBot.dll")

    member _.LogPath = logPath

    static member StartAsync(env: RealEnv) =
        task {
            let dll = BotProcess.DllPath

            if not (File.Exists dll) then
                failwith $"{dll} not found — run `make alita-build` first"

            let logPath = Path.Combine(RealEnv.artifactsDir, "bot.log")

            let psi =
                ProcessStartInfo(
                    FileName = "dotnet",
                    Arguments = $"\"{dll}\"",
                    WorkingDirectory = Path.GetDirectoryName dll,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true)

            let envVars =
                [ "DATABASE_URL", DevDb.connectionString
                  "BOT_TELEGRAM_TOKEN", env.BotToken
                  "BOT_AUTH_TOKEN", env.WebhookSecret
                  "ASPNETCORE_URLS", listenUrl
                  // Fallbacks only — bot_setting rows (DevDb.applyRealSettingsAsync) win.
                  "TARGET_CHAT_IDS", string env.TestChatId
                  "BOT_USERNAME", env.BotUsername
                  "RESPONDER_MODE", env.ResponderMode
                  "AZURE_FOUNDRY_ENDPOINT", env.AzureFoundryEndpoint
                  "AZURE_FOUNDRY_KEY", env.AzureFoundryKey
                  "GEMINI_API_KEY", env.GeminiApiKey
                  // Make sure the bot talks to the real Telegram API even if the
                  // parent shell has a fake URL exported.
                  "TELEGRAM_API_URL", "" ]

            for key, value in envVars do
                psi.Environment[key] <- value

            let log = TextWriter.Synchronized(new StreamWriter(logPath, append = false, AutoFlush = true))
            let proc = new Process(StartInfo = psi)
            proc.OutputDataReceived.Add(fun e -> if not (isNull e.Data) then log.WriteLine e.Data)
            proc.ErrorDataReceived.Add(fun e -> if not (isNull e.Data) then log.WriteLine e.Data)

            if not (proc.Start()) then
                failwith "Failed to start `dotnet AlitaBot.dll`"

            proc.BeginOutputReadLine()
            proc.BeginErrorReadLine()

            use http = new HttpClient(Timeout = TimeSpan.FromSeconds 2.)
            let deadline = DateTime.UtcNow + healthTimeout
            let mutable healthy = false

            while not healthy && DateTime.UtcNow < deadline && not proc.HasExited do
                try
                    let! resp = http.GetAsync $"{listenUrl}/healthz"
                    healthy <- resp.IsSuccessStatusCode
                with _ ->
                    ()

                if not healthy then
                    do! Task.Delay 500

            if not healthy then
                try
                    proc.Kill(entireProcessTree = true)
                with _ ->
                    ()

                log.Dispose()
                failwith $"AlitaBot did not become healthy within {healthTimeout.TotalSeconds}s — see {logPath}"

            return new BotProcess(proc, log, logPath)
        }

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            task {
                try
                    proc.Kill(entireProcessTree = true)
                    do! proc.WaitForExitAsync()
                with _ ->
                    ()

                proc.Dispose()
                log.Dispose()
            }
            |> ValueTask
