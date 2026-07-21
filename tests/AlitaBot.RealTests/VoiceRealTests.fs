namespace AlitaBot.RealTests

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Dapper
open Npgsql
open Xunit

/// End-to-end voice-transcription test: synthesizes a short Russian phrase via
/// AlitaBot's OWN alita-tts deployment (a real Azure call, not a fake), sends it as
/// a genuine voice note over MTProto, and waits for the bot's transcript reply —
/// exercising the full download -> alita-stt transcription -> reply round trip
/// against real Telegram + real Azure. STT can be slow, so timeouts here are more
/// generous than the plain-text SmokeTests.
module VoiceRealTimeouts =
    /// Voice-note round trip: Telegram upload + bot download + real STT + reply.
    let transcriptionReply = TimeSpan.FromSeconds 150.

/// The 3-word test phrase — real Azure STT has, once, misrecognized one word of it
/// ("проверка" -> "previarka" in a flake). Assertions accept ANY of the three words
/// (case-insensitive) rather than requiring the exact one that happened to survive.
module VoicePhrase =
    let words = [ "проверка"; "связи"; "алита" ]

    let containsAnyWord (text: string) =
        let lower = text.ToLowerInvariant()
        words |> List.exists lower.Contains

type VoiceRealTests(fx: RealAssemblyFixture) =
    let env = fx.Env

    let queryOne (sql: string) (param: obj) =
        task {
            use conn = new NpgsqlConnection(fx.DbConnectionString)
            let! rows = conn.QueryAsync<LogRow>(sql, param)
            return rows |> Seq.tryHead
        }

    /// Polls for the sender's transcript row ("[voice] ...") most recently logged
    /// for the test chat — used instead of exact text matching since the STT
    /// transcript's exact wording is only approximately known ahead of time.
    let awaitVoiceSenderRow () =
        task {
            let deadline = DateTime.UtcNow + Timeouts.dbSettle
            let mutable found = None

            while found.IsNone && DateTime.UtcNow < deadline do
                let! row =
                    queryOne
                        """
SELECT message_id, user_id, is_bot, reply_to_message_id, text
FROM message_log
WHERE chat_id = @chat_id AND is_bot = false AND text LIKE '[voice]%'
ORDER BY message_id DESC LIMIT 1;
"""
                        {| chat_id = env.TestChatId |}

                found <- row
                if found.IsNone then do! Task.Delay 500

            return found
        }

    let awaitBotReplyRow (userMessageId: int64) =
        task {
            let deadline = DateTime.UtcNow + Timeouts.dbSettle
            let mutable found = None

            while found.IsNone && DateTime.UtcNow < deadline do
                let! row =
                    queryOne
                        """
SELECT message_id, user_id, is_bot, reply_to_message_id, text
FROM message_log
WHERE chat_id = @chat_id AND is_bot = true AND reply_to_message_id = @rid
ORDER BY message_id LIMIT 1;
"""
                        {| chat_id = env.TestChatId; rid = userMessageId |}

                found <- row
                if found.IsNone then do! Task.Delay 500

            return found
        }

    /// Calls the real alita-tts deployment (never the fake suite) and writes the raw
    /// response bytes to a temp file under the artifacts dir. Fails loudly (with the
    /// response body) on anything but 2xx — a silent skip here would hide a broken
    /// TTS deployment behind a generic "no reply" test failure downstream.
    let synthesizeToFile (text: string) : Task<string> =
        task {
            use http = new HttpClient(Timeout = TimeSpan.FromSeconds 30.)
            let uri =
                $"{env.AzureFoundryEndpoint.TrimEnd('/')}/openai/deployments/{env.TtsDeployment}/audio/speech?api-version=2024-08-01-preview"

            use req = new HttpRequestMessage(HttpMethod.Post, uri)
            req.Headers.Add("api-key", env.AzureFoundryKey)
            let bodyJson =
                $"""{{"input":{JsonSerializer.Serialize text},"voice":"alloy","response_format":"opus"}}"""
            req.Content <- new StringContent(bodyJson, Encoding.UTF8, "application/json")

            let! resp = http.SendAsync(req)

            if not resp.IsSuccessStatusCode then
                let! body = resp.Content.ReadAsStringAsync()
                return failwith $"alita-tts synthesis failed ({int resp.StatusCode}): {body}"
            else
                let! bytes = resp.Content.ReadAsByteArrayAsync()
                let rawPath = Path.Combine(RealEnv.artifactsDir, "tts-raw.bin")
                File.WriteAllBytes(rawPath, bytes)
                return rawPath
        }

    /// Re-encodes whatever Azure returned into a clean ogg/opus container via ffmpeg —
    /// guarantees the exact container/codec a Telegram voice note expects regardless
    /// of the raw framing behind response_format=opus.
    let convertToVoiceOgg (inputPath: string) : Task<string> =
        task {
            let outPath = Path.Combine(RealEnv.artifactsDir, "tts-voice.ogg")
            if File.Exists outPath then File.Delete outPath

            let psi =
                ProcessStartInfo(
                    FileName = "ffmpeg",
                    Arguments = $"-y -i \"{inputPath}\" -c:a libopus -b:a 32k \"{outPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true)

            use proc = new Process(StartInfo = psi)
            if not (proc.Start()) then failwith "Failed to start ffmpeg — is it installed on PATH?"

            let! _stdout = proc.StandardOutput.ReadToEndAsync()
            let! stderr = proc.StandardError.ReadToEndAsync()
            do! proc.WaitForExitAsync()

            if proc.ExitCode <> 0 then
                return failwith $"ffmpeg conversion failed (exit {proc.ExitCode}):\n{stderr}"
            else
                return outPath
        }

    [<Fact>]
    member _.``real voice note gets transcribed and the bot replies with the transcript``() =
        task {
            fx.SkipUnlessUserClient()

            if String.IsNullOrWhiteSpace env.TtsDeployment || String.IsNullOrWhiteSpace env.SttDeployment then
                Assert.Skip "ALITA_TTS_DEPLOYMENT/ALITA_STT_DEPLOYMENT missing in ~/.alita-test/env"

            let phrase = "проверка связи Алита"
            let! rawPath = synthesizeToFile phrase
            let! oggPath = convertToVoiceOgg rawPath

            let! msgId = fx.UserClient.SendVoice(env.TestChatId, oggPath, 3)
            let! reply = fx.UserClient.AwaitReplyTo(env.TestChatId, msgId, VoiceRealTimeouts.transcriptionReply)

            Assert.False(String.IsNullOrWhiteSpace reply.message)
            Assert.True(
                VoicePhrase.containsAnyWord reply.message,
                $"Expected reply to contain one of {VoicePhrase.words}: {reply.message}")

            match! awaitVoiceSenderRow () with
            | None -> Assert.Fail "sender's voice transcript ('[voice] ...') never landed in message_log"
            | Some senderRow ->
                Assert.False senderRow.is_bot
                Assert.True(
                    VoicePhrase.containsAnyWord senderRow.text,
                    $"Expected sender transcript to contain one of {VoicePhrase.words}: {senderRow.text}")

                match! awaitBotReplyRow senderRow.message_id with
                | None ->
                    Assert.Fail
                        $"no bot reply row (reply_to_message_id={senderRow.message_id}) in message_log for the voice transcript"
                | Some botRow ->
                    Assert.True botRow.is_bot
                    Assert.Equal(env.BotUserId, botRow.user_id)
                    Assert.True(
                        VoicePhrase.containsAnyWord botRow.text,
                        $"Expected bot reply row to contain one of {VoicePhrase.words}: {botRow.text}")
        }
