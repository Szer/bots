namespace AlitaBot.RealTests

open System
open System.Threading.Tasks
open Dapper
open Npgsql
open Xunit

/// End-to-end music-generation test (Gemini provider slice): sends `/song <lyrics>` as a
/// genuine Telegram message and waits for the bot's AUDIO reply — exercising the full
/// command-parse -> Lyria generateContent -> (best-effort mp3 re-encode) -> sendAudio round
/// trip against real Telegram + real Gemini.
///
/// Self-skips (rather than failing) when ALITA_GEMINI_API_KEY is unset, or when it IS set
/// but Gemini music generation is billing-gated for this key's Google Cloud project (a real,
/// discovered constraint — see GeminiProbe.fs's doc comment), mirroring
/// ImageGenRealTests' own self-skip idiom for Azure's 0 image quota. The fake-suite tests
/// (tests/AlitaBot.Tests/SongTests.fs) cover the command/plumbing behavior in the meantime.
module SongRealTimeouts =
    /// Music generation is slow (Lyria inference + Telegram round trip) — same generous
    /// budget ImageGenRealTests gives image generation.
    let songReply = TimeSpan.FromSeconds 120.

type SongRealTests(fx: RealAssemblyFixture) =
    let env = fx.Env

    let queryOne (sql: string) (param: obj) =
        task {
            use conn = new NpgsqlConnection(fx.DbConnectionString)
            let! rows = conn.QueryAsync<LogRow>(sql, param)
            return rows |> Seq.tryHead
        }

    /// Polls for the user's `[song-cmd] ...` row containing `marker`.
    let awaitUserCmdRow (marker: string) =
        task {
            let deadline = DateTime.UtcNow + Timeouts.dbSettle
            let mutable found = None

            while found.IsNone && DateTime.UtcNow < deadline do
                let! row =
                    queryOne
                        """
SELECT message_id, user_id, is_bot, reply_to_message_id, text
FROM message_log
WHERE chat_id = @chat_id AND is_bot = false AND text LIKE '[song-cmd]%' AND text LIKE '%' || @marker || '%'
ORDER BY message_id DESC LIMIT 1;
"""
                        {| chat_id = env.TestChatId; marker = marker |}

                found <- row
                if found.IsNone then do! Task.Delay 500

            return found
        }

    /// Polls for the bot's `[song] ...` reply row attributed to `userMessageId`.
    let awaitSongReplyRow (userMessageId: int64) =
        task {
            let deadline = DateTime.UtcNow + Timeouts.dbSettle
            let mutable found = None

            while found.IsNone && DateTime.UtcNow < deadline do
                let! row =
                    queryOne
                        """
SELECT message_id, user_id, is_bot, reply_to_message_id, text
FROM message_log
WHERE chat_id = @chat_id AND is_bot = true AND reply_to_message_id = @rid AND text LIKE '[song]%'
ORDER BY message_id LIMIT 1;
"""
                        {| chat_id = env.TestChatId; rid = userMessageId |}

                found <- row
                if found.IsNone then do! Task.Delay 500

            return found
        }

    [<Fact>]
    member _.``real /song short lyrics gets an audio reply``() =
        task {
            fx.SkipUnlessUserClient()

            if String.IsNullOrWhiteSpace env.GeminiApiKey then
                Assert.Skip "ALITA_GEMINI_API_KEY missing in ~/.alita-test/env — no real music-gen backend"

            let! blocked = GeminiProbe.isQuotaBlocked env.GeminiApiKey "lyria-3-pro-preview"
            if blocked then
                Assert.Skip
                    "Gemini music generation is billing-gated (free_tier limit: 0) for this ALITA_GEMINI_API_KEY's Google Cloud project — see GeminiProbe.fs's doc comment"

            let marker = Guid.NewGuid().ToString "N"
            let lyrics = $"(тестовый рок) короткая тестовая песня {marker}"

            let! msgId = fx.UserClient.SendText(env.TestChatId, $"/song {lyrics}")
            let! duration, byteSize = fx.UserClient.AwaitAudioReplyTo(env.TestChatId, msgId, SongRealTimeouts.songReply)

            // Fuzzy on purpose (mirrors StretchRealTests' /say check) — what matters is
            // that SOMETHING playable came back.
            Assert.True(duration >= 0, $"expected a non-negative audio duration, got {duration}")
            Assert.True(byteSize > 0L, $"expected non-empty audio bytes, got {byteSize}")

            match! awaitUserCmdRow marker with
            | None -> Assert.Fail $"'[song-cmd]' row (marker {marker}) never landed in message_log"
            | Some userRow ->
                Assert.False userRow.is_bot

                match! awaitSongReplyRow userRow.message_id with
                | None ->
                    Assert.Fail
                        $"no '[song] ...' bot reply row (reply_to_message_id={userRow.message_id}) in message_log"
                | Some botRow ->
                    Assert.True botRow.is_bot
                    Assert.Equal(env.BotUserId, botRow.user_id)
        }
