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
    let ``img with IMAGE_PROVIDER=gemini hits the Gemini fake, not Azure, and sends a photo captioned with the composed persona reaction`` () =
        task {
            try
                do! fixture.SetBotSetting("IMAGE_PROVIDER", "gemini")
                do! fixture.ReloadSettings()
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()
                do! fixture.SetGeminiImageScript [||]
                let scriptedCaption = "готово, любуйся"
                do! fixture.SetAzureLlmScript [| LlmTestHelpers.scripted 200 (LlmTestHelpers.completionBody scriptedCaption) |]

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

                // The caption/message_log row is now the composeCaption SCRIPTED text, not
                // an echo of the raw prompt (S10 PR1, OQ3) — the Gemini image path shares
                // MediaActions.generateImage with the Azure path, so this fix applies here too.
                let! replies = botReplyRows msgId
                Assert.Contains(replies, fun (r: MessageLogRow) -> r.text = $"[image] {scriptedCaption}")
            finally
                fixture.SetBotSetting("IMAGE_PROVIDER", "azure") |> ignore
                fixture.SetGeminiImageScript [||] |> ignore
                fixture.SetAzureLlmScript [||] |> ignore
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

    // ── Error visibility (item 2: "read errors before retrying") + transient fallback
    // (item 4: staging evidence — /img failed with Gemini 503 UNAVAILABLE "high demand"
    // and the user only saw the generic RU shrug) ──────────────────────────────────────

    [<Fact>]
    let ``img scripted Gemini 500 logs the full error body and replies with the generic RU shrug`` () =
        task {
            try
                do! fixture.SetBotSetting("IMAGE_PROVIDER", "gemini")
                do! fixture.ReloadSettings()
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()
                do!
                    fixture.SetGeminiImageScript
                        [| LlmTestHelpers.scripted
                               500
                               """{"error":{"code":500,"status":"INTERNAL","message":"boom from gemini fake"}}""" |]

                let user = Tg.user(id = 9303L, username = "gemini_erin", firstName = "Erin")
                let update = Tg.groupMessage("/img нарисуй что-нибудь сломанное", user, fixture.TargetChatId)

                let! resp = fixture.SendUpdate(update)
                resp.EnsureSuccessStatusCode() |> ignore

                // The generic RU shrug, not the transient-flavored reply — a plain 500 isn't
                // classified as transient (see LlmTypes.LlmError.isTransient's doc comment).
                let! edits = fixture.GetFakeCalls("editMessageText")
                let editsToChat = edits |> Array.filter (isToChat fixture.TargetChatId)
                Assert.Contains(editsToChat, fun c -> (jsonString c "text") = "Не получилось нарисовать 🙁")
                Assert.DoesNotContain(editsToChat, fun c -> (jsonString c "text").Contains "перегружена")

                // The full scripted error body — model name + status + the exact message —
                // must be logged at Warning BEFORE that fallback reply, not swallowed.
                let! logs = fixture.GetBotLogs()
                Assert.Contains("gemini-test-image", logs)
                Assert.Contains("boom from gemini fake", logs)
                Assert.Contains("500", logs)
            finally
                fixture.SetBotSetting("IMAGE_PROVIDER", "azure") |> ignore
                fixture.SetGeminiImageScript [||] |> ignore
                fixture.ReloadSettings() |> ignore
        }

    [<Fact>]
    let ``img scripted Gemini 503 UNAVAILABLE replies with the transient RU fallback`` () =
        task {
            try
                do! fixture.SetBotSetting("IMAGE_PROVIDER", "gemini")
                do! fixture.ReloadSettings()
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()
                do!
                    fixture.SetGeminiImageScript
                        [| LlmTestHelpers.scripted
                               503
                               """{"error":{"code":503,"status":"UNAVAILABLE","message":"The model is overloaded. Please try again later."}}""" |]

                let user = Tg.user(id = 9304L, username = "gemini_frank", firstName = "Frank")
                let update = Tg.groupMessage("/img нарисуй перегруженную модель", user, fixture.TargetChatId)

                let! resp = fixture.SendUpdate(update)
                resp.EnsureSuccessStatusCode() |> ignore

                let! edits = fixture.GetFakeCalls("editMessageText")
                let editsToChat = edits |> Array.filter (isToChat fixture.TargetChatId)
                Assert.Contains(
                    editsToChat,
                    fun c -> (jsonString c "text") = "Модель перегружена, попробуй ещё раз через минутку 🙏")
                Assert.DoesNotContain(editsToChat, fun c -> (jsonString c "text") = "Не получилось нарисовать 🙁")
            finally
                fixture.SetBotSetting("IMAGE_PROVIDER", "azure") |> ignore
                fixture.SetGeminiImageScript [||] |> ignore
                fixture.ReloadSettings() |> ignore
        }

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            ValueTask(task {
                do! fixture.SetBotSetting("IMAGE_PROVIDER", "azure")
                do! fixture.SetGeminiImageScript [||]
                do! fixture.SetAzureImageScript [||]
                do! fixture.SetAzureLlmScript [||]
                do! fixture.ReloadSettings()
            } :> Task)

        member _.DisposeAsync() =
            ValueTask(task {
                do! fixture.SetBotSetting("IMAGE_PROVIDER", "azure")
                do! fixture.SetGeminiImageScript [||]
                do! fixture.SetAzureImageScript [||]
                do! fixture.SetAzureLlmScript [||]
                do! fixture.ReloadSettings()
            } :> Task)
