namespace AlitaBot.Tests

open System
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks
open BotTestInfra
open Funogram.Telegram.Types
open Xunit

open CommandsTestHelpers

/// Slice 9 (stretch): `/say` voice replies, admin-gated `/sql` analytics, cost footer.
type StretchTests(fixture: AlitaTestContainers) =

    let botReplyRows (replyToMessageId: int64) =
        fixture.Query<MessageLogRow>(
            """
SELECT chat_id, message_id, user_id, username, display_name, is_bot, reply_to_message_id, text
FROM message_log WHERE chat_id = @cid AND is_bot = TRUE AND reply_to_message_id = @mid
ORDER BY message_id
""",
            {| cid = fixture.TargetChatId; mid = replyToMessageId |})

    let senderRow (chatId: int64) (messageId: int64) =
        fixture.QuerySingleOrDefault<MessageLogRow>(
            """
SELECT chat_id, message_id, user_id, username, display_name, is_bot, reply_to_message_id, text
FROM message_log WHERE chat_id = @cid AND message_id = @mid
""",
            {| cid = chatId; mid = messageId |})

    /// A tiny buffer starting with the Ogg container magic ("OggS") — enough for
    /// BotService.isOggContainer's 4-byte check to pass, so these tests exercise the
    /// sendVoice fast path (never ffmpeg, which the fake-test container doesn't ship).
    let oggBytesBase64 () =
        Convert.ToBase64String [| 0x4Fuy; 0x67uy; 0x67uy; 0x53uy; 1uy; 2uy; 3uy; 4uy; 5uy |]

    let sqlJson (sql: string) = JsonSerializer.Serialize {| sql = sql |}

    // ── /say ─────────────────────────────────────────────────────────────────

    [<Fact>]
    let ``say with scripted TTS bytes sends a voice note and logs the bot reply`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()
            do! fixture.SetAzureTtsScript [| scripted 200 (oggBytesBase64 ()) |]

            let user = Tg.user(id = 9201L, username = "say_alice", firstName = "Alice")
            let update = Tg.groupMessage("/say привет", user, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! voiceSends = fixture.GetFakeCalls("sendVoice")
            Assert.NotEmpty(voiceSends |> Array.filter (isToChat fixture.TargetChatId))

            let! ttsCalls = fixture.GetAzureOcrCalls()
            Assert.Contains(ttsCalls, fun (c: FakeCall) -> c.Url.Contains "audio/speech")

            let! cmdRow = senderRow fixture.TargetChatId msgId
            Assert.NotNull(box cmdRow)
            Assert.Equal("[say-cmd] привет", cmdRow.text)

            let! replyRows = botReplyRows msgId
            Assert.NotEmpty replyRows
            Assert.Equal("[voice] привет", replyRows[0].text)
        }

    [<Fact>]
    let ``say with an invalid voice replying to a message refuses with a RU hint and never calls TTS`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()

            let user = Tg.user(id = 9202L, username = "say_bob", firstName = "Bob")
            let repliedMsg =
                Message.Create(
                    messageId = 500001L,
                    date = DateTime.UtcNow,
                    chat = Tg.groupChat(id = fixture.TargetChatId),
                    from = user,
                    text = "какой-то текст для озвучки")
            let update = Tg.groupMessage("/say notavoice", user, fixture.TargetChatId, replyToMessage = repliedMsg)

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! sends = fixture.GetFakeCalls("sendMessage")
            let toChat = sends |> Array.filter (isToChat fixture.TargetChatId)
            Assert.Contains(toChat, fun c -> (jsonString c "text").Contains "Не знаю такой голос")

            let! ttsCalls = fixture.GetAzureOcrCalls()
            Assert.DoesNotContain(ttsCalls, fun (c: FakeCall) -> c.Url.Contains "audio/speech")

            let! voiceSends = fixture.GetFakeCalls("sendVoice")
            Assert.Empty(voiceSends |> Array.filter (isToChat fixture.TargetChatId))
        }

    [<Fact>]
    let ``say over SAY_MAX_CHARS refuses without calling TTS`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()

            let user = Tg.user(id = 9203L, username = "say_carol", firstName = "Carol")
            let longText = String.replicate 501 "a"
            let update = Tg.groupMessage($"/say {longText}", user, fixture.TargetChatId)

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! sends = fixture.GetFakeCalls("sendMessage")
            let toChat = sends |> Array.filter (isToChat fixture.TargetChatId)
            Assert.Contains(toChat, fun c -> (jsonString c "text").Contains "максимум")

            let! ttsCalls = fixture.GetAzureOcrCalls()
            Assert.DoesNotContain(ttsCalls, fun (c: FakeCall) -> c.Url.Contains "audio/speech")
        }

    // ── /sql ─────────────────────────────────────────────────────────────────

    [<Fact>]
    let ``sql as a non-admin refuses and never calls the LLM`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()
            do! fixture.SetBotSetting("ADMIN_USER_IDS", "[]")
            do! fixture.ReloadSettings()

            let user = Tg.user(id = 9204L, username = "sql_dave", firstName = "Dave")
            let update = Tg.groupMessage("/sql сколько сообщений в этом чате?", user, fixture.TargetChatId)

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! sends = fixture.GetFakeCalls("sendMessage")
            let toChat = sends |> Array.filter (isToChat fixture.TargetChatId)
            Assert.Contains(toChat, fun c -> (jsonString c "text").Contains "Куда")

            let! llmCalls = fixture.GetAzureLlmCalls()
            Assert.Empty llmCalls
        }

    [<Fact>]
    let ``sql as an admin renders the query result as a table`` () =
        task {
            try
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()
                do! fixture.SetBotSetting("ADMIN_USER_IDS", $"[{AlitaTestConfig.adminUserId}]")
                do! fixture.ReloadSettings()
                do! fixture.SetAzureLlmScript [| scripted 200 (nonStreamCompletionBody (sqlJson "SELECT 1 AS n")) |]

                let user = Tg.user(id = AlitaTestConfig.adminUserId, username = "sql_admin", firstName = "Admin")
                let update = Tg.groupMessage("/sql сколько будет один?", user, fixture.TargetChatId)
                let msgId = update.Message.Value.MessageId

                let! resp = fixture.SendUpdate(update)
                resp.EnsureSuccessStatusCode() |> ignore

                let! sends = fixture.GetFakeCalls("sendMessage")
                let toChat = sends |> Array.filter (isToChat fixture.TargetChatId)
                Assert.Contains(toChat, fun c -> (jsonString c "text").Contains "1")

                let! replyRows = botReplyRows msgId
                Assert.NotEmpty replyRows
                Assert.Contains("1", replyRows[0].text)
            finally
                fixture.SetBotSetting("ADMIN_USER_IDS", "[]") |> ignore
                fixture.ReloadSettings() |> ignore
        }

    [<Fact>]
    let ``sql rejects a scripted DML attempt without executing it`` () =
        task {
            try
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()
                do! fixture.SetBotSetting("ADMIN_USER_IDS", $"[{AlitaTestConfig.adminUserId}]")
                do! fixture.ReloadSettings()
                do! fixture.SetAzureLlmScript [| scripted 200 (nonStreamCompletionBody (sqlJson "DROP TABLE karma")) |]

                let user = Tg.user(id = AlitaTestConfig.adminUserId, username = "sql_admin2", firstName = "Admin")
                let update = Tg.groupMessage("/sql удали таблицу karma", user, fixture.TargetChatId)

                let! resp = fixture.SendUpdate(update)
                resp.EnsureSuccessStatusCode() |> ignore

                let! sends = fixture.GetFakeCalls("sendMessage")
                let toChat = sends |> Array.filter (isToChat fixture.TargetChatId)
                Assert.Contains(toChat, fun c -> (jsonString c "text").Contains "Отклонено")

                // karma must still exist and be queryable — a real DROP would make this throw.
                let! count = fixture.QuerySingleOrDefault<int64>("SELECT COUNT(*) FROM karma", {||})
                Assert.True(count >= 0L)
            finally
                fixture.SetBotSetting("ADMIN_USER_IDS", "[]") |> ignore
                fixture.ReloadSettings() |> ignore
        }

    // ── Cost footer ──────────────────────────────────────────────────────────

    [<Fact>]
    let ``cost footer ON appends a cost line via an extra edit, never landing in message_log`` () =
        task {
            try
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()
                do! fixture.SetBotSetting("RESPONDER_MODE", "llm")
                do! fixture.SetBotSetting("STREAM_MODE", "plain")
                do! fixture.SetBotSetting("COST_FOOTER_ENABLED", "true")
                do! fixture.ReloadSettings()
                do! fixture.SetAzureLlmScript [| scripted 200 (nonStreamCompletionBody "тестовый ответ") |]

                let user = Tg.user(id = 9205L, username = "footer_erin", firstName = "Erin")
                let update = Tg.groupMessage("алита привет", user, fixture.TargetChatId)
                let msgId = update.Message.Value.MessageId

                let! resp = fixture.SendUpdate(update)
                resp.EnsureSuccessStatusCode() |> ignore

                // Cost footer is delivered by ONE extra editMessageText after the normal
                // send (ResponderService.maybeAppendCostFooter, "append at final edit").
                let! edits = fixture.GetFakeCalls("editMessageText")
                let toChat = edits |> Array.filter (isToChat fixture.TargetChatId)
                Assert.NotEmpty toChat
                let editedText = jsonString toChat[toChat.Length - 1] "text"
                Assert.True(
                    Regex.IsMatch(editedText, @"⛽ \$\d+\\?\.\d{4}"),
                    $"expected a '⛽ $X.XXXX' cost footer in: {editedText}")

                let! replyRows = botReplyRows msgId
                Assert.NotEmpty replyRows
                Assert.DoesNotContain("⛽", replyRows[0].text)
            finally
                fixture.SetBotSetting("RESPONDER_MODE", "echo") |> ignore
                fixture.SetBotSetting("STREAM_MODE", "edit") |> ignore
                fixture.SetBotSetting("COST_FOOTER_ENABLED", "false") |> ignore
                fixture.ReloadSettings() |> ignore
        }

    [<Fact>]
    let ``cost footer OFF never edits the reply again`` () =
        task {
            try
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()
                do! fixture.SetBotSetting("RESPONDER_MODE", "llm")
                do! fixture.SetBotSetting("STREAM_MODE", "plain")
                do! fixture.SetBotSetting("COST_FOOTER_ENABLED", "false")
                do! fixture.ReloadSettings()
                do! fixture.SetAzureLlmScript [| scripted 200 (nonStreamCompletionBody "другой ответ") |]

                let user = Tg.user(id = 9206L, username = "footer_frank", firstName = "Frank")
                let update = Tg.groupMessage("алита привет ещё раз", user, fixture.TargetChatId)
                let msgId = update.Message.Value.MessageId

                let! resp = fixture.SendUpdate(update)
                resp.EnsureSuccessStatusCode() |> ignore

                let! sends = fixture.GetFakeCalls("sendMessage")
                Assert.NotEmpty(sends |> Array.filter (isToChat fixture.TargetChatId))

                let! edits = fixture.GetFakeCalls("editMessageText")
                Assert.Empty(edits |> Array.filter (isToChat fixture.TargetChatId))

                let! replyRows = botReplyRows msgId
                Assert.NotEmpty replyRows
                Assert.DoesNotContain("⛽", replyRows[0].text)
            finally
                fixture.SetBotSetting("RESPONDER_MODE", "echo") |> ignore
                fixture.SetBotSetting("STREAM_MODE", "edit") |> ignore
                fixture.ReloadSettings() |> ignore
        }

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            ValueTask(task {
                do! fixture.SetAzureTtsScript [||]
                do! fixture.SetBotSetting("ADMIN_USER_IDS", "[]")
                do! fixture.SetBotSetting("COST_FOOTER_ENABLED", "false")
                do! fixture.SetBotSetting("RESPONDER_MODE", "echo")
                do! fixture.SetBotSetting("STREAM_MODE", "edit")
                do! fixture.ReloadSettings()
            } :> Task)

        member _.DisposeAsync() =
            ValueTask(task {
                do! fixture.SetAzureTtsScript [||]
                do! fixture.SetBotSetting("ADMIN_USER_IDS", "[]")
                do! fixture.SetBotSetting("COST_FOOTER_ENABLED", "false")
                do! fixture.SetBotSetting("RESPONDER_MODE", "echo")
                do! fixture.SetBotSetting("STREAM_MODE", "edit")
                do! fixture.ReloadSettings()
            } :> Task)
