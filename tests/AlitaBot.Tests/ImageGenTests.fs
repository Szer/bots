namespace AlitaBot.Tests

open System
open System.Text.RegularExpressions
open System.Threading.Tasks
open BotTestInfra
open Funogram.Telegram.Types
open Xunit

module ImageGenTestHelpers =
    let scripted (status: int) (body: string) : AzureScriptedResponse =
        { status = status; body = body; delayMs = 0; errorMode = "" }

    let jsonString (call: FakeCall) (property: string) =
        use doc = System.Text.Json.JsonDocument.Parse(call.Body)
        match doc.RootElement.TryGetProperty property with
        | true, v when v.ValueKind = Text.Json.JsonValueKind.String -> v.GetString() |> Option.ofObj |> Option.defaultValue ""
        | _ -> ""

    let isToChat (chatId: int64) (call: FakeCall) =
        use doc = System.Text.Json.JsonDocument.Parse(call.Body)
        match doc.RootElement.TryGetProperty "chat_id" with
        | true, v when v.ValueKind = Text.Json.JsonValueKind.Number -> v.GetInt64() = chatId
        | _ -> false

    /// Extracts the `imageBytes=N` count the images/edits fake logs into its call body
    /// (see FakeAzureOcrApi.Handlers.handleImagesEdits) — 0 when absent/unparseable.
    let imageBytesCaptured (call: FakeCall) =
        let m = Regex.Match(call.Body, @"imageBytes=(\d+)")
        if m.Success then int m.Groups[1].Value else 0

/// Image-generation (Phase-1 Slice 3) tests against the fake images/generations +
/// images/edits endpoints. `/img`/`!img` never touch RESPONDER_MODE — left at the
/// container default ("echo") throughout, since image commands bypass ResponderService
/// entirely.
type ImageGenTests(fixture: AlitaTestContainers) =

    let userRow (chatId: int64) (messageId: int64) =
        fixture.QuerySingleOrDefault<MessageLogRow>(
            """
SELECT chat_id, message_id, user_id, username, display_name, is_bot, reply_to_message_id, text
FROM message_log WHERE chat_id = @cid AND message_id = @mid
""",
            {| cid = chatId; mid = messageId |})

    let botReplyRows (replyToMessageId: int64) =
        fixture.Query<MessageLogRow>(
            """
SELECT chat_id, message_id, user_id, username, display_name, is_bot, reply_to_message_id, text
FROM message_log WHERE chat_id = @cid AND is_bot = TRUE AND reply_to_message_id = @mid
ORDER BY message_id
""",
            {| cid = fixture.TargetChatId; mid = replyToMessageId |})

    [<Fact>]
    let ``img prompt sends a photo captioned with the composed persona reaction (not the raw prompt), logs both rows, and cleans up the placeholder`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()
            let scriptedCaption = "держи, свежее"
            do! fixture.SetAzureLlmScript [| ImageGenTestHelpers.scripted 200 (LlmTestHelpers.completionBody scriptedCaption) |]

            let user = Tg.user(id = 7001L, username = "img_alice", firstName = "Alice")
            let prompt = "нарисуй рыжего кота на подоконнике"
            let update = Tg.groupMessage($"/img {prompt}", user, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! photoSends = fixture.GetFakeCalls("sendPhoto")
            let toChat = photoSends |> Array.filter (ImageGenTestHelpers.isToChat fixture.TargetChatId)
            Assert.NotEmpty toChat
            // The caption is MediaActions.composeCaption's SCRIPTED output, never a
            // (truncated) echo of the raw prompt (S10 PR1, OQ3).
            Assert.Contains(toChat, fun c -> (ImageGenTestHelpers.jsonString c "caption") = scriptedCaption)
            Assert.DoesNotContain(toChat, fun c -> (ImageGenTestHelpers.jsonString c "caption").Contains prompt)

            // The "рисую..." placeholder must be sent then cleaned up (deleted).
            let! sends = fixture.GetFakeCalls("sendMessage")
            Assert.Contains(
                sends |> Array.filter (ImageGenTestHelpers.isToChat fixture.TargetChatId),
                fun c -> (ImageGenTestHelpers.jsonString c "text") = "рисую...")
            let! deletes = fixture.GetFakeCalls("deleteMessage")
            Assert.NotEmpty deletes

            let! row = userRow fixture.TargetChatId msgId
            Assert.NotNull(box row)
            Assert.False row.is_bot
            Assert.Equal($"[img-cmd] {prompt}", row.text)

            // message_log's bot row now logs the real (scripted) caption, not the prompt.
            let! replies = botReplyRows msgId
            Assert.Contains(replies, fun (r: MessageLogRow) -> r.text = $"[image] {scriptedCaption}")
        }

    [<Fact>]
    let ``img replying to a photo calls images-edits with a non-empty source image`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()

            let user = Tg.user(id = 7002L, username = "img_bob", firstName = "Bob")
            let fileId = "img-source-photo-1"
            do! fixture.SetTelegramFile(fileId, [| 1uy; 2uy; 3uy; 4uy; 5uy; 6uy |])

            let photoUpdate = Tg.groupPhotoMessage(user, fixture.TargetChatId, caption = "исходное фото", fileId = fileId)
            let photoMsg = photoUpdate.Message.Value
            let! photoResp = fixture.SendUpdate(photoUpdate)
            photoResp.EnsureSuccessStatusCode() |> ignore

            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()

            let replyUpdate = Tg.groupMessage("/img перекрась в синий", user, fixture.TargetChatId, replyToMessage = photoMsg)
            let! resp = fixture.SendUpdate(replyUpdate)
            resp.EnsureSuccessStatusCode() |> ignore

            let! azureCalls = fixture.GetAzureOcrCalls()
            let editCalls = azureCalls |> Array.filter (fun c -> c.Url.Contains "images/edits")
            Assert.NotEmpty editCalls
            Assert.True(
                editCalls |> Array.exists (fun c -> ImageGenTestHelpers.imageBytesCaptured c > 0),
                "expected the images/edits fake to capture a non-empty source image")

            let! photoSends = fixture.GetFakeCalls("sendPhoto")
            Assert.NotEmpty(photoSends |> Array.filter (ImageGenTestHelpers.isToChat fixture.TargetChatId))
        }

    [<Fact>]
    let ``IMAGE_GEN_ENABLED=false replies with a disabled notice and never calls Azure images endpoints`` () =
        task {
            try
                do! fixture.SetBotSetting("IMAGE_GEN_ENABLED", "false")
                do! fixture.ReloadSettings()
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()

                let user = Tg.user(id = 7003L, username = "img_carol", firstName = "Carol")
                let update = Tg.groupMessage("/img что-нибудь", user, fixture.TargetChatId)

                let! resp = fixture.SendUpdate(update)
                resp.EnsureSuccessStatusCode() |> ignore

                let! sends = fixture.GetFakeCalls("sendMessage")
                let toChat = sends |> Array.filter (ImageGenTestHelpers.isToChat fixture.TargetChatId)
                Assert.Contains(toChat, fun c -> (ImageGenTestHelpers.jsonString c "text").Contains "выключен")

                let! photoSends = fixture.GetFakeCalls("sendPhoto")
                Assert.Empty photoSends

                let! azureCalls = fixture.GetAzureOcrCalls()
                Assert.DoesNotContain(azureCalls, fun (c: FakeCall) -> c.Url.Contains "images/generations" || c.Url.Contains "images/edits")
            finally
                fixture.SetBotSetting("IMAGE_GEN_ENABLED", "true") |> ignore
                fixture.ReloadSettings() |> ignore
        }

    [<Fact>]
    let ``img with an empty prompt replies with a usage hint and never calls Azure`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()

            let user = Tg.user(id = 7004L, username = "img_dave", firstName = "Dave")
            let update = Tg.groupMessage("/img", user, fixture.TargetChatId)

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! sends = fixture.GetFakeCalls("sendMessage")
            let toChat = sends |> Array.filter (ImageGenTestHelpers.isToChat fixture.TargetChatId)
            Assert.Contains(toChat, fun c -> (ImageGenTestHelpers.jsonString c "text").Contains "/img")

            let! photoSends = fixture.GetFakeCalls("sendPhoto")
            Assert.Empty photoSends

            let! azureCalls = fixture.GetAzureOcrCalls()
            Assert.DoesNotContain(azureCalls, fun (c: FakeCall) -> c.Url.Contains "images/generations" || c.Url.Contains "images/edits")
        }

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            ValueTask(task {
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()
                do! fixture.SetAzureImageScript [||]
                do! fixture.SetAzureLlmScript [||]
                do! fixture.SetBotSetting("IMAGE_GEN_ENABLED", "true")
                do! fixture.ReloadSettings()
            } :> Task)

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(task {
                do! fixture.SetAzureImageScript [||]
                do! fixture.SetAzureLlmScript [||]
                do! fixture.SetBotSetting("IMAGE_GEN_ENABLED", "true")
                do! fixture.ReloadSettings()
            } :> Task)
