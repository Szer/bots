namespace AlitaBot.RealTests

open System
open System.Threading.Tasks
open Dapper
open Npgsql
open Xunit

/// End-to-end image-generation test (Phase-1 Slice 3): sends `/img <prompt> <guid>` as a
/// genuine Telegram message and waits for the bot's PHOTO reply — exercising the full
/// command-parse -> Azure images/generations -> download -> sendPhoto round trip against
/// real Telegram + real Azure.
///
/// Self-skips (rather than failing) when ALITA_IMAGE_DEPLOYMENT is unset: at S3 deploy time
/// every gpt-image-* variant (and dall-e-3, the documented fallback) had 0 quota in this
/// subscription/region — see AlitaBot/docs/TECH-DEBT.md — so there is no real deployment to
/// exercise yet. The fake-suite tests (tests/AlitaBot.Tests/ImageGenTests.fs) cover the
/// command/plumbing behavior in the meantime.
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
        task {
            fx.SkipUnlessUserClient()

            if String.IsNullOrWhiteSpace env.ImageDeployment then
                Assert.Skip
                    "ALITA_IMAGE_DEPLOYMENT missing in ~/.alita-test/env — no real image-gen deployment exists yet (quota denied, see AlitaBot/docs/TECH-DEBT.md)"

            let marker = Guid.NewGuid().ToString "N"
            let prompt = $"нарисуй красный квадрат {marker}"

            let! msgId = fx.UserClient.SendText(env.TestChatId, $"/img {prompt}")
            let! photoReply = fx.UserClient.AwaitPhotoReplyTo(env.TestChatId, msgId, ImageGenRealTimeouts.imageReply)

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
        }
