namespace AlitaBot.Tests

open System.Threading.Tasks
open BotTestInfra
open Funogram.Telegram.Types
open Xunit

open CommandsTestHelpers

/// Gemini provider slice: `/img` routed to Gemini (Nano Banana) vs Azure by the
/// IMAGE_PROVIDER bot_setting — see Llm/GeminiProvider.fs's ImageGenRouter. The fixture's
/// baseline IMAGE_PROVIDER is "azure" (ContainerTestBase.fs, kept that way so the existing
/// ImageGenTests.fs assertions against the Azure fake are undisturbed) — every test here
/// flips it to "gemini" explicitly and restores "azure" afterward.
type GeminiTests(fixture: AlitaTestContainers) =

    let botReplyRows (replyToMessageId: int64) =
        fixture.Query<MessageLogRow>(
            """
SELECT chat_id, message_id, user_id, username, display_name, is_bot, reply_to_message_id, text
FROM message_log WHERE chat_id = @cid AND is_bot = TRUE AND reply_to_message_id = @mid
ORDER BY message_id
""",
            {| cid = fixture.TargetChatId; mid = replyToMessageId |})

    [<Fact>]
    let ``img with IMAGE_PROVIDER=gemini hits the Gemini fake, not Azure, and sends a photo`` () =
        task {
            try
                do! fixture.SetBotSetting("IMAGE_PROVIDER", "gemini")
                do! fixture.ReloadSettings()
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()
                do! fixture.SetGeminiImageScript [||]

                let user = Tg.user(id = 9301L, username = "gemini_alice", firstName = "Alice")
                let prompt = "нарисуй красный квадрат"
                let update = Tg.groupMessage($"/img {prompt}", user, fixture.TargetChatId)
                let msgId = update.Message.Value.MessageId

                let! resp = fixture.SendUpdate(update)
                resp.EnsureSuccessStatusCode() |> ignore

                let! photoSends = fixture.GetFakeCalls("sendPhoto")
                Assert.NotEmpty(photoSends |> Array.filter (isToChat fixture.TargetChatId))

                let! geminiCalls = fixture.GetGeminiCalls()
                Assert.Contains(geminiCalls, fun (c: FakeCall) -> c.Url.Contains "generateContent")

                let! azureCalls = fixture.GetAzureOcrCalls()
                Assert.DoesNotContain(
                    azureCalls,
                    fun (c: FakeCall) -> c.Url.Contains "images/generations" || c.Url.Contains "images/edits")

                let! replies = botReplyRows msgId
                Assert.Contains(replies, fun (r: MessageLogRow) -> r.text.StartsWith "[image] " && r.text.Contains prompt)
            finally
                fixture.SetBotSetting("IMAGE_PROVIDER", "azure") |> ignore
                fixture.SetGeminiImageScript [||] |> ignore
                fixture.ReloadSettings() |> ignore
        }

    [<Fact>]
    let ``img provider switched back to azure hits the Azure images fake`` () =
        task {
            try
                // Flip to gemini then back to azure — exercises the actual switch, not just
                // the fixture's baseline default.
                do! fixture.SetBotSetting("IMAGE_PROVIDER", "gemini")
                do! fixture.ReloadSettings()
                do! fixture.SetBotSetting("IMAGE_PROVIDER", "azure")
                do! fixture.ReloadSettings()
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()
                do! fixture.SetAzureImageScript [||]

                let user = Tg.user(id = 9302L, username = "gemini_bob", firstName = "Bob")
                let update = Tg.groupMessage("/img нарисуй синий круг", user, fixture.TargetChatId)

                let! resp = fixture.SendUpdate(update)
                resp.EnsureSuccessStatusCode() |> ignore

                let! photoSends = fixture.GetFakeCalls("sendPhoto")
                Assert.NotEmpty(photoSends |> Array.filter (isToChat fixture.TargetChatId))

                let! azureCalls = fixture.GetAzureOcrCalls()
                Assert.Contains(azureCalls, fun (c: FakeCall) -> c.Url.Contains "images/generations")

                let! geminiCalls = fixture.GetGeminiCalls()
                Assert.Empty geminiCalls
            finally
                fixture.SetBotSetting("IMAGE_PROVIDER", "azure") |> ignore
                fixture.ReloadSettings() |> ignore
        }

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            ValueTask(task {
                do! fixture.SetBotSetting("IMAGE_PROVIDER", "azure")
                do! fixture.SetGeminiImageScript [||]
                do! fixture.SetAzureImageScript [||]
                do! fixture.ReloadSettings()
            } :> Task)

        member _.DisposeAsync() =
            ValueTask(task {
                do! fixture.SetBotSetting("IMAGE_PROVIDER", "azure")
                do! fixture.SetGeminiImageScript [||]
                do! fixture.SetAzureImageScript [||]
                do! fixture.ReloadSettings()
            } :> Task)
