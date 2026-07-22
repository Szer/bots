namespace AlitaBot.RealTests

open System
open System.Threading.Tasks
open Dapper
open Npgsql
open Xunit

/// End-to-end image-generation test (Phase-1 Slice 3, extended by the Gemini provider
/// slice): sends `/img <prompt> <guid>` as a genuine Telegram message and waits for the
/// bot's PHOTO reply — exercising the full command-parse -> images backend -> download ->
/// sendPhoto round trip against real Telegram + a real image-gen backend.
///
/// Backend selection: Gemini (Nano Banana) when ALITA_GEMINI_API_KEY is configured —
/// DevDb.applyRealSettingsAsync then also pushes IMAGE_PROVIDER=gemini — else falls back to
/// Azure (ALITA_IMAGE_DEPLOYMENT), self-skipping when NEITHER is set: at S3 deploy time
/// every gpt-image-* variant (and dall-e-3, the documented fallback) had 0 quota in this
/// subscription/region — see AlitaBot/docs/TECH-DEBT.md. When Gemini IS configured but its
/// own Google Cloud project has no billing enabled for image generation (a real, discovered
/// constraint — see GeminiProbe.fs's doc comment), this ALSO self-skips rather than
/// hard-failing on an external blocker outside this PR's control. The fake-suite tests
/// (tests/AlitaBot.Tests/ImageGenTests.fs, GeminiTests.fs) cover the command/plumbing
/// behavior for both backends in the meantime.
module ImageGenRealTimeouts =
    /// Image generation is slow (several seconds of actual model inference on top of the
    /// Telegram round trip) — matches the plan's "timeout >= 120s" requirement.
    let imageReply = TimeSpan.FromSeconds 120.

type ImageGenRealTests(fx: RealAssemblyFixture) =
    let env = fx.Env

    let queryOne (sql: string) (param: obj) =
        task {
            use conn = new NpgsqlConnection(fx.DbConnectionString)
            let! rows = conn.QueryAsync<LogRow>(sql, param)
            return rows |> Seq.tryHead
        }

    /// Polls for the user's `[img-cmd] ...` row containing `marker`.
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
WHERE chat_id = @chat_id AND is_bot = false AND text LIKE '[img-cmd]%' AND text LIKE '%' || @marker || '%'
ORDER BY message_id DESC LIMIT 1;
"""
                        {| chat_id = env.TestChatId; marker = marker |}

                found <- row
                if found.IsNone then do! Task.Delay 500

            return found
        }

    /// Polls for the bot's `[image] ...` reply row attributed to `userMessageId`.
    let awaitImageReplyRow (userMessageId: int64) =
        task {
            let deadline = DateTime.UtcNow + Timeouts.dbSettle
            let mutable found = None

            while found.IsNone && DateTime.UtcNow < deadline do
                let! row =
                    queryOne
                        """
SELECT message_id, user_id, is_bot, reply_to_message_id, text
FROM message_log
WHERE chat_id = @chat_id AND is_bot = true AND reply_to_message_id = @rid AND text LIKE '[image]%'
ORDER BY message_id LIMIT 1;
"""
                        {| chat_id = env.TestChatId; rid = userMessageId |}

                found <- row
                if found.IsNone then do! Task.Delay 500

            return found
        }

    [<Fact>]
    member _.``real /img prompt gets a photo reply captioned with the prompt``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let usingGemini = not (String.IsNullOrWhiteSpace env.GeminiApiKey)

            if not usingGemini && String.IsNullOrWhiteSpace env.ImageDeployment then
                Assert.Skip
                    "Neither ALITA_GEMINI_API_KEY nor ALITA_IMAGE_DEPLOYMENT is set in ~/.alita-test/env — no real image-gen backend exists yet (Azure quota denied, see AlitaBot/docs/TECH-DEBT.md)"

            if usingGemini then
                let! blocked = GeminiProbe.isQuotaBlocked env.GeminiApiKey "gemini-3.1-flash-image"
                if blocked then
                    Assert.Skip
                        "Gemini image generation is billing-gated (free_tier limit: 0) for this ALITA_GEMINI_API_KEY's Google Cloud project — see GeminiProbe.fs's doc comment"

            let marker = Guid.NewGuid().ToString "N"
            let prompt = $"нарисуй красный квадрат {marker}"

            let! msgId = fx.UserClient.SendText(env.TestChatId, $"/img {prompt}")
            // The "рисую..." placeholder is the first reply to msgId — captured so a
            // later failure (edited in place, never a new message) can be told apart
            // from "still generating"; see AwaitMediaOrPlaceholderEdit's doc comment.
            let! placeholderReply = fx.UserClient.AwaitReplyTo(env.TestChatId, msgId, Timeouts.reply)

            match!
                fx.UserClient.AwaitMediaOrPlaceholderEdit
                    (fun t -> fx.UserClient.TryAwaitPhotoReplyTo(env.TestChatId, msgId, t))
                    env.TestChatId
                    placeholderReply.id
                    placeholderReply.message
                    ImageGenRealTimeouts.imageReply
            with
            | Choice2Of2 editedText ->
                // Item 4/5 (staging feedback): a Gemini 503 "high demand" failure gets a
                // distinct RU reply and is skipped, not retried/failed — see
                // GeminiTransientDetection's doc comment ("not waste money" on a second
                // paid attempt against an upstream capacity blip outside this repo's control).
                match GeminiTransientDetection.classify placeholderReply.message editedText with
                | GeminiTransientDetection.ReplyOutcome.Transient ->
                    Assert.Skip
                        $"Gemini 503 high-demand — transient upstream capacity, skipped to not waste money (bot replied: {editedText})"
                | _ ->
                    Assert.Fail $"/img failed with a non-transient error — bot replied: {editedText}"
            | Choice1Of2 photoReply ->
                Assert.False(String.IsNullOrWhiteSpace photoReply.message)
                let caption = photoReply.message
                Assert.True(
                    caption.Contains "красный" || caption.Contains marker,
                    $"expected the photo caption to reference the prompt, got: {caption}")

                match! awaitUserCmdRow marker with
                | None -> Assert.Fail $"'[img-cmd]' row (marker {marker}) never landed in message_log"
                | Some userRow ->
                    Assert.False userRow.is_bot

                    match! awaitImageReplyRow userRow.message_id with
                    | None ->
                        Assert.Fail
                            $"no '[image] ...' bot reply row (reply_to_message_id={userRow.message_id}) in message_log"
                    | Some botRow ->
                        Assert.True botRow.is_bot
                        Assert.Equal(env.BotUserId, botRow.user_id)
        })
