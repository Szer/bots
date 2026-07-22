/// Brings up the local dev database (Postgres :15433 + Flyway + settings seed)
/// via src/alita-bot/docker-compose.dev.yml. Never torn down between tests —
/// `make alita-clean` handles teardown.
module AlitaBot.RealTests.DevDb

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open Dapper
open Npgsql

let composeFile = Path.Combine(RealEnv.repoRoot, "src", "alita-bot", "docker-compose.dev.yml")

/// Service-role connection string; creds from src/alita-bot/init.sql, port from the compose file.
let connectionString =
    "Host=127.0.0.1;Port=15433;Database=alita_bot;Username=alita_bot_service;Password=alita_bot_service;Include Error Detail=true"

let private readyTimeout = TimeSpan.FromSeconds 90.

/// `docker` is often just an interactive shell alias for podman — resolve the
/// real binary from PATH (override with ALITA_CONTAINER_CLI).
let private containerCli =
    match Environment.GetEnvironmentVariable "ALITA_CONTAINER_CLI" with
    | null
    | "" ->
        let onPath name =
            (Environment.GetEnvironmentVariable "PATH" |> string).Split ':'
            |> Array.exists (fun dir -> dir <> "" && File.Exists(Path.Combine(dir, name)))

        if onPath "docker" then "docker"
        elif onPath "podman" then "podman"
        else "docker"
    | cli -> cli

let private runCompose (args: string) =
    task {
        let psi =
            ProcessStartInfo(
                FileName = containerCli,
                Arguments = $"compose -f \"{composeFile}\" {args}",
                WorkingDirectory = RealEnv.repoRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true)

        use proc = new Process(StartInfo = psi)

        if not (proc.Start()) then
            failwith $"Failed to start `{containerCli} compose`"

        let! stdout = proc.StandardOutput.ReadToEndAsync()
        let! stderr = proc.StandardError.ReadToEndAsync()
        do! proc.WaitForExitAsync()

        if proc.ExitCode <> 0 then
            failwith $"`docker compose {args}` exited with {proc.ExitCode}:\n{stdout}\n{stderr}"
    }

/// `docker compose up -d` (postgres + flyway + seed), then wait until the seed
/// has landed (bot_setting non-empty). Idempotent — cheap when already up.
let upAsync () =
    task {
        do! runCompose "up -d"

        let deadline = DateTime.UtcNow + readyTimeout
        let mutable ready = false
        let mutable lastError = "no attempt made"

        while not ready && DateTime.UtcNow < deadline do
            try
                use conn = new NpgsqlConnection(connectionString)
                do! conn.OpenAsync()
                let! count = conn.ExecuteScalarAsync<int64> "SELECT COUNT(*) FROM bot_setting"

                if count > 0L then
                    ready <- true
                else
                    lastError <- "bot_setting is still empty (seed not finished)"
                    do! Task.Delay 1000
            with e ->
                lastError <- e.Message
                do! Task.Delay 1000

        if not ready then
            failwith $"dev DB not ready within {readyTimeout.TotalSeconds}s: {lastError}"
    }

let private upsertSql =
    """
INSERT INTO bot_setting (key, value, type, feature_group, description)
VALUES (@key, @value, @typ, @grp, 'set by AlitaBot.RealTests harness')
ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = NOW();
"""

/// Point the bot at the real test chat/bot and the requested responder mode.
/// Required: bot_setting values (even empty ones from the seed) win over env
/// fallbacks in AlitaBot's buildBotConf.
let applyRealSettingsAsync (env: RealEnv) =
    task {
        use conn = new NpgsqlConnection(connectionString)
        do! conn.OpenAsync()

        let settings =
            [ "TARGET_CHAT_IDS", string env.TestChatId, "FREE_FORM", "telegram"
              "BOT_USERNAME", env.BotUsername, "FREE_FORM", "telegram"
              "RESPONDER_MODE", env.ResponderMode, "FREE_FORM", "llm"
              "STREAM_MODE", env.StreamMode, "FREE_FORM", "llm"
              // Slice 5b: DossierRealTests triggers the nightly job via POST /test/run-job,
              // which 404s outside TEST_MODE (Program.fs) — force it on for every real-test
              // run, same as RESPONDER_MODE/STREAM_MODE above (dev-bot-settings.sql's local-
              // dev seed defaults it to 'false', matching production posture).
              "TEST_MODE", "true", "FEATURE_FLAG", "diagnostics" ]
            @ (if String.IsNullOrWhiteSpace env.SttDeployment then [] else [ "STT_DEPLOYMENT", env.SttDeployment, "FREE_FORM", "llm" ])
            @ (if String.IsNullOrWhiteSpace env.TtsDeployment then [] else [ "TTS_DEPLOYMENT", env.TtsDeployment, "FREE_FORM", "llm" ])
            @ (if String.IsNullOrWhiteSpace env.ImageDeployment then [] else [ "IMAGE_DEPLOYMENT", env.ImageDeployment, "FREE_FORM", "llm" ])
            @ (if String.IsNullOrWhiteSpace env.EmbeddingDeployment then [] else [ "EMBEDDING_DEPLOYMENT", env.EmbeddingDeployment, "FREE_FORM", "llm" ])

        for key, value, typ, grp in settings do
            let! _ = conn.ExecuteAsync(upsertSql, {| key = key; value = value; typ = typ; grp = grp |})
            ()
    }
