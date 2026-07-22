namespace AlitaBot.Tests

open System
open System.Text.Json
open BotTestInfra
open Funogram.Telegram.Types
open Xunit

[<CLIMutable>]
type MessageLogRow =
    { chat_id: int64
      message_id: int64
      user_id: int64
      username: string | null
      display_name: string
      is_bot: bool
      reply_to_message_id: Nullable<int64>
      text: string }

module SkeletonHelpers =
    /// True when the call body targets `chatId` and its text contains `substring`.
    let callHasText (chatId: int64) (substring: string) (call: FakeCall) =
        use doc = JsonDocument.Parse(call.Body)
        let root = doc.RootElement
        let chatOk =
            match root.TryGetProperty("chat_id") with
            | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt64() = chatId
            | _ -> false
        let textOk =
            match root.TryGetProperty("text") with
            | true, v when v.ValueKind = JsonValueKind.String -> v.GetString().Contains(substring)
            | _ -> false
        chatOk && textOk

type SkeletonTests(fixture: AlitaTestContainers) =

    let selectRowSql =
        """
SELECT chat_id, message_id, user_id, username, display_name, is_bot, reply_to_message_id, text
FROM message_log WHERE chat_id = @cid AND message_id = @mid
"""

    [<Fact>]
    let ``Mention triggers a pong reply and both rows are logged`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 3001L, username = "alice", firstName = "Alice")
            let mention = $"@{fixture.BotUsername}"
            let text = $"{mention} hi"
            let entities = [| MessageEntity.Create(``type`` = "mention", offset = 0L, length = int64 mention.Length) |]
            let update = Tg.quickMsg(text = text, chat = Tg.chat(id = fixture.TargetChatId), from = user, entities = entities)
            let msgId = update.Message.Value.MessageId

            let! _ = fixture.SendUpdate(update)

            // Fake captured the pong reply
            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(
                calls |> Array.exists (SkeletonHelpers.callHasText fixture.TargetChatId "pong"),
                $"Expected a sendMessage call with 'pong' to chat {fixture.TargetChatId}")

            // Bot's reply row is in message_log with is_bot=true, linked to the original message
            let! replyRow =
                fixture.QuerySingleOrDefault<MessageLogRow>(
                    "SELECT chat_id, message_id, user_id, username, display_name, is_bot, reply_to_message_id, text
                     FROM message_log WHERE chat_id = @cid AND is_bot = TRUE AND reply_to_message_id = @mid",
                    {| cid = fixture.TargetChatId; mid = msgId |})
            Assert.NotNull(box replyRow)
            Assert.True(replyRow.is_bot)
            Assert.Contains("pong", replyRow.text)
            Assert.Contains(text, replyRow.text)
        }

    [<Fact>]
    let ``Plain message is logged with user attribution and not replied to`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 3002L, username = "bob", firstName = "Bob")
            let update = Tg.groupMessage("just chatting about weather", user, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId

            let! _ = fixture.SendUpdate(update)

            let! row =
                fixture.QuerySingleOrDefault<MessageLogRow>(
                    selectRowSql,
                    {| cid = fixture.TargetChatId; mid = msgId |})
            Assert.NotNull(box row)
            Assert.Equal(3002L, row.user_id)
            Assert.Equal("bob", row.username)
            Assert.Equal("Bob", row.display_name)
            Assert.False(row.is_bot)
            Assert.Equal("just chatting about weather", row.text)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.Empty(calls)
        }

    [<Fact>]
    let ``Duplicate webhook delivery of the same update produces only one reply`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 3004L, username = "carol", firstName = "Carol")
            let mention = $"@{fixture.BotUsername}"
            let text = $"{mention} hi again"
            let entities = [| MessageEntity.Create(``type`` = "mention", offset = 0L, length = int64 mention.Length) |]
            let update = Tg.quickMsg(text = text, chat = Tg.chat(id = fixture.TargetChatId), from = user, entities = entities)
            let msgId = update.Message.Value.MessageId

            // Simulate Telegram redelivering the exact same update (e.g. after a slow
            // response held the webhook connection past Telegram's retry timeout).
            let! _ = fixture.SendUpdate(update)
            let! _ = fixture.SendUpdate(update)

            let! calls = fixture.GetFakeCalls("sendMessage")
            let pongCalls = calls |> Array.filter (SkeletonHelpers.callHasText fixture.TargetChatId "pong")
            Assert.Equal(1, pongCalls.Length)

            let! replyRows =
                fixture.Query<MessageLogRow>(
                    "SELECT chat_id, message_id, user_id, username, display_name, is_bot, reply_to_message_id, text
                     FROM message_log WHERE chat_id = @cid AND is_bot = TRUE AND reply_to_message_id = @mid",
                    {| cid = fixture.TargetChatId; mid = msgId |})
            Assert.Single(replyRows) |> ignore
        }

    [<Fact>]
    let ``Message from a non-target chat is neither logged nor replied to`` () =
        task {
            do! fixture.ClearFakeCalls()
            let strangerChatId = -999888L
            let user = Tg.user(id = 3003L, username = "eve", firstName = "Eve")
            let update = Tg.groupMessage($"@{fixture.BotUsername} алита привет", user, strangerChatId)
            let msgId = update.Message.Value.MessageId

            let! _ = fixture.SendUpdate(update)

            let! row =
                fixture.QuerySingleOrDefault<MessageLogRow>(
                    selectRowSql,
                    {| cid = strangerChatId; mid = msgId |})
            Assert.Null(box row)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.Empty(calls)
        }
