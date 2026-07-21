namespace AlitaBot.Tests

open System
open System.Text.Json
open System.Threading.Tasks
open BotTestInfra
open Funogram.Telegram.Types
open Xunit

module VoiceTestHelpers =
    let scripted (status: int) (body: string) : AzureScriptedResponse =
        { status = status; body = body; delayMs = 0; errorMode = "" }

    /// The fake's /openai/deployments/{d}/audio/transcriptions response shape
    /// (AzureFoundrySpeech.Transcribe parses response_format=json's {"text": "..."}).
    let sttBody (text: string) =
        let contentJson = JsonSerializer.Serialize(text)
        $"""{{"text":{contentJson}}}"""

    let jsonString (call: FakeCall) (property: string) =
        use doc = JsonDocument.Parse(call.Body)
        match doc.RootElement.TryGetProperty property with
        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() |> Option.ofObj |> Option.defaultValue ""
        | _ -> ""

    let isToChat (chatId: int64) (call: FakeCall) =
        use doc = JsonDocument.Parse(call.Body)
        match doc.RootElement.TryGetProperty "chat_id" with
        | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt64() = chatId
        | _ -> false

    let hasExpandableBlockquote (call: FakeCall) =
        use doc = JsonDocument.Parse(call.Body)
        match doc.RootElement.TryGetProperty "entities" with
        | true, arr when arr.ValueKind = JsonValueKind.Array ->
            arr.EnumerateArray()
            |> Seq.exists (fun e ->
                match e.TryGetProperty "type" with
                | true, t when t.ValueKind = JsonValueKind.String -> t.GetString() = "expandable_blockquote"
                | _ -> false)
        | _ -> false

/// Voice/VideoNote/Audio transcription tests against the fake Azure audio/transcriptions
/// (STT) backend. Each test that changes VOICE_TRANSCRIBE_ENABLED restores it to "true"
/// afterwards so other test classes keep their expected default.
type VoiceTests(fixture: AlitaTestContainers) =

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

    [<Fact>]
    let ``Voice message gets transcribed and replied to as an expandable blockquote`` () =
        task {
            do! fixture.ClearFakeCalls()
            let transcript = "привет, это тестовая расшифровка голосового сообщения"
            do! fixture.SetAzureSttScript [| VoiceTestHelpers.scripted 200 (VoiceTestHelpers.sttBody transcript) |]

            let user = Tg.user(id = 5001L, username = "voice_alice", firstName = "Alice")
            let update = Tg.groupVoiceMessage(user, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! sends = fixture.GetFakeCalls("sendMessage")
            let blockquoteReplies =
                sends
                |> Array.filter (VoiceTestHelpers.isToChat fixture.TargetChatId)
                |> Array.filter VoiceTestHelpers.hasExpandableBlockquote
            Assert.NotEmpty(blockquoteReplies)

            let replyText = VoiceTestHelpers.jsonString blockquoteReplies[0] "text"
            Assert.StartsWith("🎙️", replyText)
            Assert.Contains(transcript, replyText)

            let! replyRows = botReplyRows msgId
            Assert.Contains(replyRows, fun (r: MessageLogRow) -> r.text.StartsWith "🎙️" && r.text.Contains transcript)
        }

    [<Fact>]
    let ``Voice transcript and the bot's reply both land in message_log`` () =
        task {
            do! fixture.ClearFakeCalls()
            let transcript = "запись для проверки message_log"
            do! fixture.SetAzureSttScript [| VoiceTestHelpers.scripted 200 (VoiceTestHelpers.sttBody transcript) |]

            let user = Tg.user(id = 5002L, username = "voice_bob", firstName = "Bob")
            let update = Tg.groupVoiceMessage(user, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! sender = senderRow fixture.TargetChatId msgId
            Assert.NotNull(box sender)
            Assert.False sender.is_bot
            Assert.Equal(5002L, sender.user_id)
            Assert.Equal($"[voice] {transcript}", sender.text)

            let! replyRows = botReplyRows msgId
            Assert.NotEmpty replyRows
            Assert.True(replyRows[0].is_bot)
            Assert.Contains(transcript, replyRows[0].text)
        }

    [<Fact>]
    let ``VOICE_TRANSCRIBE_ENABLED=false skips transcription entirely`` () =
        task {
            try
                do! fixture.SetBotSetting("VOICE_TRANSCRIBE_ENABLED", "false")
                do! fixture.ReloadSettings()
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()
                do! fixture.SetAzureSttScript [| VoiceTestHelpers.scripted 200 (VoiceTestHelpers.sttBody "should never be requested") |]

                let user = Tg.user(id = 5003L, username = "voice_disabled", firstName = "Carol")
                let update = Tg.groupVoiceMessage(user, fixture.TargetChatId)
                let msgId = update.Message.Value.MessageId

                let! resp = fixture.SendUpdate(update)
                resp.EnsureSuccessStatusCode() |> ignore

                let! sends = fixture.GetFakeCalls("sendMessage")
                Assert.Empty(sends |> Array.filter (VoiceTestHelpers.isToChat fixture.TargetChatId))

                let! azureCalls = fixture.GetAzureOcrCalls()
                Assert.DoesNotContain(azureCalls, fun (c: FakeCall) -> c.Url.Contains "audio/transcriptions")

                let! sender = senderRow fixture.TargetChatId msgId
                Assert.Null(box sender)
            finally
                fixture.SetBotSetting("VOICE_TRANSCRIBE_ENABLED", "true") |> ignore
                fixture.ReloadSettings() |> ignore
        }

    [<Fact>]
    let ``Voice transcript containing the bot's name also triggers the echo responder`` () =
        task {
            do! fixture.ClearFakeCalls()
            let transcript = "алита, ты тут?"
            do! fixture.SetAzureSttScript [| VoiceTestHelpers.scripted 200 (VoiceTestHelpers.sttBody transcript) |]

            let user = Tg.user(id = 5004L, username = "voice_dave", firstName = "Dave")
            let update = Tg.groupVoiceMessage(user, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! sends = fixture.GetFakeCalls("sendMessage")
            let toTargetChat = sends |> Array.filter (VoiceTestHelpers.isToChat fixture.TargetChatId)

            // The blockquote transcript reply...
            Assert.NotEmpty(toTargetChat |> Array.filter VoiceTestHelpers.hasExpandableBlockquote)
            // ...AND a separate echo reply triggered by the transcript's "алита".
            let pongReplies =
                toTargetChat
                |> Array.filter (fun c -> (VoiceTestHelpers.jsonString c "text").StartsWith "pong: ")
            Assert.NotEmpty(pongReplies)
            Assert.Contains(transcript, VoiceTestHelpers.jsonString pongReplies[0] "text")

            let! replyRows = botReplyRows msgId
            Assert.True(replyRows.Length >= 2, "expected both the transcript reply and the triggered echo reply logged")
        }

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            ValueTask(task {
                do! fixture.SetAzureSttScript [||]
                do! fixture.SetBotSetting("VOICE_TRANSCRIBE_ENABLED", "true")
                do! fixture.SetBotSetting("RESPONDER_MODE", "echo")
                do! fixture.ReloadSettings()
            } :> Task)

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(task {
                do! fixture.SetAzureSttScript [||]
                do! fixture.SetBotSetting("VOICE_TRANSCRIBE_ENABLED", "true")
                do! fixture.SetBotSetting("RESPONDER_MODE", "echo")
                do! fixture.ReloadSettings()
            } :> Task)
