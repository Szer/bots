namespace AlitaBot.Tests

open System
open System.Text.Json
open System.Threading.Tasks
open BotTestInfra
open Funogram.Telegram.Types
open Xunit

module VisionTestHelpers =
    let scripted (status: int) (body: string) : AzureScriptedResponse =
        { status = status; body = body; delayMs = 0; errorMode = "" }

    /// Same shape as LlmTestHelpers.completionBody — the fake splits it into SSE
    /// chunks for streamed (RESPONDER_MODE=llm) calls.
    let completionBody (content: string) =
        let contentJson = JsonSerializer.Serialize(content)
        $"""{{"id":"chatcmpl-vision","object":"chat.completion","model":"gpt-5-mini-2025-08-07","choices":[{{"index":0,"finish_reason":"stop","message":{{"role":"assistant","content":{contentJson}}}}}],"usage":{{"prompt_tokens":150,"completion_tokens":30,"total_tokens":180}}}}"""

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

    /// True if any message's `content` is an array (i.e. multimodal, not the
    /// plain-string shape used for text-only messages) containing an image_url
    /// part whose url is a base64 data: URL.
    let hasImageUrlPart (call: FakeCall) =
        use doc = JsonDocument.Parse(call.Body)
        doc.RootElement.GetProperty("messages").EnumerateArray()
        |> Seq.exists (fun m ->
            match m.TryGetProperty "content" with
            | true, c when c.ValueKind = JsonValueKind.Array ->
                c.EnumerateArray()
                |> Seq.exists (fun part ->
                    match part.TryGetProperty "type" with
                    | true, t when t.ValueKind = JsonValueKind.String && t.GetString() = "image_url" ->
                        match part.TryGetProperty "image_url" with
                        | true, img ->
                            match img.TryGetProperty "url" with
                            | true, u when u.ValueKind = JsonValueKind.String ->
                                u.GetString().StartsWith("data:", StringComparison.Ordinal)
                            | _ -> false
                        | _ -> false
                    | _ -> false)
            | _ -> false)

    /// True if any content-array `text` part (or a plain-string content) contains `needle`.
    let contentContainsText (call: FakeCall) (needle: string) =
        use doc = JsonDocument.Parse(call.Body)
        doc.RootElement.GetProperty("messages").EnumerateArray()
        |> Seq.exists (fun m ->
            match m.TryGetProperty "content" with
            | true, c when c.ValueKind = JsonValueKind.String -> c.GetString().Contains(needle, StringComparison.Ordinal)
            | true, c when c.ValueKind = JsonValueKind.Array ->
                c.EnumerateArray()
                |> Seq.exists (fun part ->
                    match part.TryGetProperty "type" with
                    | true, t when t.ValueKind = JsonValueKind.String && t.GetString() = "text" ->
                        match part.TryGetProperty "text" with
                        | true, tx when tx.ValueKind = JsonValueKind.String -> tx.GetString().Contains(needle, StringComparison.Ordinal)
                        | _ -> false
                    | _ -> false)
            | _ -> false)

/// Vision (Phase-1 Slice 2) tests: photos flowing into the LLM conversation as
/// image_url content parts. Each test flips RESPONDER_MODE to llm for its duration
/// and restores echo after, same convention as LlmTests.
type VisionTests(fixture: AlitaTestContainers) =

    let msgRow (chatId: int64) (messageId: int64) =
        fixture.QuerySingleOrDefault<MessageLogRow>(
            """
SELECT chat_id, message_id, user_id, username, display_name, is_bot, reply_to_message_id, text
FROM message_log WHERE chat_id = @cid AND message_id = @mid
""",
            {| cid = chatId; mid = messageId |})

    [<Fact>]
    let ``Triggered photo message with a caption mention attaches an image_url part and logs [photo] caption`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()

            let user = Tg.user(id = 6001L, username = "vision_alice", firstName = "Alice")
            let mention = $"@{fixture.BotUsername}"
            let caption = $"{mention} что на фото?"
            let entities = [| MessageEntity.Create(``type`` = "mention", offset = 0L, length = int64 mention.Length) |]
            let fileId = "vision-photo-caption-1"
            do! fixture.SetTelegramFile(fileId, [| 1uy; 2uy; 3uy; 4uy; 5uy |])
            do! fixture.SetAzureLlmScript [| VisionTestHelpers.scripted 200 (VisionTestHelpers.completionBody "Вижу картинку") |]

            let update = Tg.groupPhotoMessage(user, fixture.TargetChatId, caption = caption, fileId = fileId, captionEntities = entities)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! llmCalls = fixture.GetAzureLlmCalls()
            Assert.NotEmpty llmCalls
            Assert.Contains(llmCalls, VisionTestHelpers.hasImageUrlPart)
            Assert.Contains(llmCalls, fun c -> VisionTestHelpers.contentContainsText c caption)

            let! sends = fixture.GetFakeCalls("sendMessage")
            Assert.NotEmpty(sends |> Array.filter (VisionTestHelpers.isToChat fixture.TargetChatId))

            let! row = msgRow fixture.TargetChatId msgId
            Assert.NotNull(box row)
            Assert.False row.is_bot
            Assert.Equal($"[photo] {caption}", row.text)
        }

    [<Fact>]
    let ``Triggered text reply to a photo message attaches the replied-to image`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()

            let user = Tg.user(id = 6002L, username = "vision_bob", firstName = "Bob")
            let fileId = "vision-photo-reply-1"
            do! fixture.SetTelegramFile(fileId, [| 6uy; 7uy; 8uy |])

            // The photo was posted earlier (untriggered, no mention in its caption).
            let photoUpdate = Tg.groupPhotoMessage(user, fixture.TargetChatId, caption = "оригинальное фото", fileId = fileId)
            let photoMsg = photoUpdate.Message.Value
            let! photoResp = fixture.SendUpdate(photoUpdate)
            photoResp.EnsureSuccessStatusCode() |> ignore

            do! fixture.ClearFakeCalls()
            do! fixture.SetAzureLlmScript [| VisionTestHelpers.scripted 200 (VisionTestHelpers.completionBody "На фото что-то видно") |]

            let replyUpdate = Tg.groupMessage("алита, что на фото?", user, fixture.TargetChatId, replyToMessage = photoMsg)
            let replyMsgId = replyUpdate.Message.Value.MessageId

            let! resp = fixture.SendUpdate(replyUpdate)
            resp.EnsureSuccessStatusCode() |> ignore

            let! llmCalls = fixture.GetAzureLlmCalls()
            Assert.NotEmpty llmCalls
            Assert.Contains(llmCalls, VisionTestHelpers.hasImageUrlPart)

            let! sends = fixture.GetFakeCalls("sendMessage")
            Assert.NotEmpty(sends |> Array.filter (VisionTestHelpers.isToChat fixture.TargetChatId))

            let! row = msgRow fixture.TargetChatId replyMsgId
            Assert.NotNull(box row)
            Assert.False row.is_bot
        }

    [<Fact>]
    let ``VISION_ENABLED=false sends no image part but still replies text-only`` () =
        task {
            try
                do! fixture.SetBotSetting("VISION_ENABLED", "false")
                do! fixture.ReloadSettings()
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()

                let user = Tg.user(id = 6003L, username = "vision_carol", firstName = "Carol")
                let mention = $"@{fixture.BotUsername}"
                let caption = $"{mention} что тут?"
                let entities = [| MessageEntity.Create(``type`` = "mention", offset = 0L, length = int64 mention.Length) |]
                let fileId = "vision-photo-disabled-1"
                do! fixture.SetTelegramFile(fileId, [| 9uy; 9uy; 9uy |])
                do! fixture.SetAzureLlmScript [| VisionTestHelpers.scripted 200 (VisionTestHelpers.completionBody "Просто текстовый ответ") |]

                let update = Tg.groupPhotoMessage(user, fixture.TargetChatId, caption = caption, fileId = fileId, captionEntities = entities)

                let! resp = fixture.SendUpdate(update)
                resp.EnsureSuccessStatusCode() |> ignore

                let! llmCalls = fixture.GetAzureLlmCalls()
                Assert.NotEmpty llmCalls
                Assert.DoesNotContain(llmCalls, VisionTestHelpers.hasImageUrlPart)

                let! sends = fixture.GetFakeCalls("sendMessage")
                let toTargetChat = sends |> Array.filter (VisionTestHelpers.isToChat fixture.TargetChatId)
                Assert.NotEmpty toTargetChat
            finally
                fixture.SetBotSetting("VISION_ENABLED", "true") |> ignore
                fixture.ReloadSettings() |> ignore
        }

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            ValueTask(task {
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()
                do! fixture.SetAzureLlmScript [||]
                do! fixture.SetAzureLlmStreamOptions(0, 0, 0)
                do! fixture.SetBotSetting("RESPONDER_MODE", "llm")
                do! fixture.SetBotSetting("VISION_ENABLED", "true")
                do! fixture.ReloadSettings()
            } :> Task)

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(task {
                do! fixture.SetAzureLlmScript [||]
                do! fixture.SetAzureLlmStreamOptions(0, 0, 0)
                do! fixture.SetBotSetting("RESPONDER_MODE", "echo")
                do! fixture.SetBotSetting("VISION_ENABLED", "true")
                do! fixture.ReloadSettings()
            } :> Task)
