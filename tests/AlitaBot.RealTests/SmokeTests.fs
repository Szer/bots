namespace AlitaBot.RealTests

open System
open System.Net.Http
open System.Threading.Tasks
open Dapper
open Npgsql
open Xunit

/// Named timeout constants (anti-flake: no magic numbers scattered in tests).
module Timeouts =
    /// Real Telegram + (later) real LLM round trip.
    let reply = TimeSpan.FromSeconds 90.
    /// Bounded negative check: how long we insist the bot stays silent.
    let noReply = TimeSpan.FromSeconds 20.
    /// Streaming settles once no edit arrived for this long.
    let editQuiet = TimeSpan.FromSeconds 5.
    /// The bot logs its own reply just after sending it — allow the row to land.
    /// In practice the insert lands within ~100-300ms; 15s leaves comfortable margin
    /// without materially slowing failing-test feedback.
    let dbSettle = TimeSpan.FromSeconds 15.

[<CLIMutable>]
type LogRow =
    { message_id: int64
      user_id: int64
      is_bot: bool
      reply_to_message_id: Nullable<int64>
      text: string }

/// The four M3 smoke tests from the plan + one user-client-free plumbing test.
///
/// DB correlation is done ENTIRELY within the Bot-API message_id domain (marker
/// text, then reply_to_message_id chaining) — never against MTProto message ids.
/// For this test chat (a basic "group", not a supergroup/channel) Telegram's Bot
/// API message_id and WTelegramClient's (MTProto) message.id are DIFFERENT
/// numbering domains with a constant-but-drifting offset (empirically observed:
/// +4 in one run, +5 the next, for the identical physical messages — some
/// invisible/service update consumes an MTProto id between runs). Comparing an
/// MTProto id against a Postgres-logged message_id — as earlier code did — reads
/// as "no matching row" even though the row is there, seconds after being
/// written; it looks like a slow/missing DB write but isn't one. Marker-text +
/// reply_to_message_id correlation sidesteps the mismatch and also survives LLM
/// mode, where the bot's reply text won't echo the marker back verbatim.
type SmokeTests(fx: RealAssemblyFixture) =

    let env = fx.Env

    let queryOne (sql: string) (param: obj) =
        task {
            use conn = new NpgsqlConnection(fx.DbConnectionString)
            let! rows = conn.QueryAsync<LogRow>(sql, param)
            return rows |> Seq.tryHead
        }

    /// Polls for the user's own logged row (is_bot = false) whose text contains
    /// `marker` — reliable in both echo and LLM mode, since it's verbatim what we sent.
    let awaitUserRow (marker: string) =
        task {
            let deadline = DateTime.UtcNow + Timeouts.dbSettle
            let mutable found = None

            while found.IsNone && DateTime.UtcNow < deadline do
                let! row =
                    queryOne
                        """
SELECT message_id, user_id, is_bot, reply_to_message_id, text
FROM message_log
WHERE chat_id = @chat_id AND is_bot = false AND text LIKE '%' || @marker || '%'
ORDER BY message_id LIMIT 1;
"""
                        {| chat_id = env.TestChatId; marker = marker |}

                found <- row

                if found.IsNone then
                    do! Task.Delay 500

            return found
        }

    /// Polls for the bot's reply row attributed (via reply_to_message_id, Bot-API
    /// domain) to `userMessageId` — not by text, since an LLM reply won't contain the marker.
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

                if found.IsNone then
                    do! Task.Delay 500

            return found
        }

    [<Fact>]
    member _.``plumbing - public healthz reachable through tunnel and webhook registered``() =
        task {
            fx.SkipUnlessCore()

            use http = new HttpClient(Timeout = TimeSpan.FromSeconds 15.)
            http.DefaultRequestHeaders.Add("ngrok-skip-browser-warning", "1")
            let! body = http.GetStringAsync $"https://{env.NgrokDomain}/healthz"
            Assert.Equal("OK", body)

            let! info = fx.Webhook.GetInfoAsync()
            Assert.Equal($"https://{env.NgrokDomain}/bot", info.GetProperty("url").GetString())
        }

    [<Fact>]
    member _.``mention triggers a reply``() =
        task {
            fx.SkipUnlessUserClient()

            let marker = Guid.NewGuid().ToString "N"
            let! msgId = fx.UserClient.SendText(env.TestChatId, $"@{env.BotUsername} ping {marker}")
            let! reply = fx.UserClient.AwaitReplyTo(env.TestChatId, msgId, Timeouts.reply)
            Assert.False(String.IsNullOrWhiteSpace reply.message)
        }

    [<Fact>]
    member _.``mention exchange is fully logged with attribution``() =
        task {
            fx.SkipUnlessUserClient()

            let marker = Guid.NewGuid().ToString "N"
            let! msgId = fx.UserClient.SendText(env.TestChatId, $"@{env.BotUsername} лог {marker}")
            let! _reply = fx.UserClient.AwaitReplyTo(env.TestChatId, msgId, Timeouts.reply)

            match! awaitUserRow marker with
            | None -> Assert.Fail $"user's own message (marker {marker}) never landed in message_log"
            | Some userRow ->
                Assert.False userRow.is_bot
                Assert.Contains(marker, userRow.text)

                match! awaitBotReplyRow userRow.message_id with
                | None ->
                    Assert.Fail
                        $"no message_log row replying (reply_to_message_id={userRow.message_id}) to the marker {marker} message"
                | Some botRow ->
                    Assert.True botRow.is_bot
                    Assert.Equal(env.BotUserId, botRow.user_id)
        }

    [<Fact>]
    member _.``non-mention is logged but not answered``() =
        task {
            fx.SkipUnlessUserClient()

            let marker = Guid.NewGuid().ToString "N"
            // No @mention and no bot-name trigger word — must stay unanswered.
            let! msgId = fx.UserClient.SendText(env.TestChatId, $"просто сообщение в чат {marker}")

            let! reply = fx.UserClient.TryAwaitReplyTo(env.TestChatId, msgId, Timeouts.noReply)
            Assert.True(reply.IsNone, "bot must not answer a non-mention")

            match! awaitUserRow marker with
            | None -> Assert.Fail $"message (marker {marker}) never landed in message_log"
            | Some row ->
                Assert.False row.is_bot
                Assert.Contains(marker, row.text)
        }

    [<Fact>]
    member _.``streamed reply settles into a final text``() =
        task {
            fx.SkipUnlessUserClient()

            let marker = Guid.NewGuid().ToString "N"
            let! msgId = fx.UserClient.SendText(env.TestChatId, $"@{env.BotUsername} расскажи что-нибудь {marker}")
            let! reply = fx.UserClient.AwaitReplyTo(env.TestChatId, msgId, Timeouts.reply)

            let! finalText = fx.UserClient.AwaitEditsSettled(env.TestChatId, reply.id, Timeouts.editQuiet)

            if env.ResponderMode = "llm" then
                // Streaming renderer must converge on a non-empty final text.
                Assert.False(String.IsNullOrWhiteSpace finalText)
            else
                // Echo mode: exactly one reply, never edited, echoing the marker.
                Assert.Contains(marker, finalText)
        }
