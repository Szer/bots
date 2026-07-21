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
    let dbSettle = TimeSpan.FromSeconds 10.

[<CLIMutable>]
type LogRow =
    { message_id: int64
      user_id: int64
      is_bot: bool
      reply_to_message_id: Nullable<int64>
      text: string }

/// The four M3 smoke tests from the plan + one user-client-free plumbing test.
/// Correlation is primarily via reply_to msg id, GUID markers secondary.
type SmokeTests(fx: RealAssemblyFixture) =

    let env = fx.Env

    let queryLog (param: obj) =
        task {
            use conn = new NpgsqlConnection(fx.DbConnectionString)

            let! rows =
                conn.QueryAsync<LogRow>(
                    """
SELECT message_id, user_id, is_bot, reply_to_message_id, text
FROM message_log
WHERE chat_id = @chat_id AND message_id = ANY(@message_ids)
ORDER BY message_id;
""",
                    param)

            return rows |> Seq.toArray
        }

    /// Polls message_log until `expected` rows for `messageIds` exist (or dbSettle elapses).
    let awaitLogRows (messageIds: int64[]) (expected: int) =
        task {
            let deadline = DateTime.UtcNow + Timeouts.dbSettle
            let mutable rows = [||]

            while rows.Length < expected && DateTime.UtcNow < deadline do
                let! found = queryLog {| chat_id = env.TestChatId; message_ids = messageIds |}
                rows <- found

                if rows.Length < expected then
                    do! Task.Delay 500

            return rows
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
            let! reply = fx.UserClient.AwaitReplyTo(env.TestChatId, msgId, Timeouts.reply)

            let! rows = awaitLogRows [| int64 msgId; int64 reply.id |] 2
            Assert.Equal(2, rows.Length)

            let userRow = rows |> Array.find (fun r -> r.message_id = int64 msgId)
            Assert.False userRow.is_bot
            Assert.Contains(marker, userRow.text)

            let botRow = rows |> Array.find (fun r -> r.message_id = int64 reply.id)
            Assert.True botRow.is_bot
            Assert.Equal(env.BotUserId, botRow.user_id)
            Assert.Equal(Nullable(int64 msgId), botRow.reply_to_message_id)
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

            let! rows = awaitLogRows [| int64 msgId |] 1
            let row = Assert.Single rows
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
