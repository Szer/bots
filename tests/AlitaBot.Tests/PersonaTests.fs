namespace AlitaBot.Tests

open System
open System.Text.Json
open System.Threading.Tasks
open BotTestInfra
open Funogram.Telegram.Types
open Xunit

open CommandsTestHelpers

/// Slice 6: outcome router (weighted reply/silence/emoji roll for TRIGGERED non-command
/// messages), MarkdownV2 final-message rendering (+ plain-text fallback on a Telegram
/// 400), rewriter pass, and reply-context enrichment.
type PersonaTests(fixture: AlitaTestContainers) =

    let botReplyRows (replyToMessageId: int64) =
        fixture.Query<MessageLogRow>(
            """
SELECT chat_id, message_id, user_id, username, display_name, is_bot, reply_to_message_id, text
FROM message_log WHERE chat_id = @cid AND is_bot = TRUE AND reply_to_message_id = @mid
ORDER BY message_id
""",
            {| cid = fixture.TargetChatId; mid = replyToMessageId |})

    let mentionUpdate (userId: int64) (username: string) (tail: string) =
        let user = Tg.user(id = userId, username = username, firstName = username)
        let mention = $"@{fixture.BotUsername}"
        let text = $"{mention} {tail}"
        let entities = [| MessageEntity.Create(``type`` = "mention", offset = 0L, length = int64 mention.Length) |]
        Tg.quickMsg(text = text, chat = Tg.chat(id = fixture.TargetChatId), from = user, entities = entities)

    /// Polls (up to `timeoutMs`) for at least one `setMessageReaction` call — same idiom as
    /// ProactiveTests.awaitReactions, duplicated locally rather than shared (both files
    /// already duplicate small polling helpers this way, see e.g. awaitSendsToChat).
    let awaitReactions (timeoutMs: int) =
        task {
            let deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(float timeoutMs)
            let mutable reactions = [||]
            while reactions.Length = 0 && DateTime.UtcNow < deadline do
                let! r = fixture.GetFakeCalls("setMessageReaction")
                reactions <- r
                if reactions.Length = 0 then do! Task.Delay 150
            return reactions
        }

    let reactedEmoji (reactions: FakeCall[]) =
        use doc = JsonDocument.Parse(reactions[0].Body: string)
        let reactionArr = doc.RootElement.GetProperty("reaction")
        reactionArr[0].GetProperty("emoji").GetString()

    // ── Outcome router ───────────────────────────────────────────────────────

    [<Fact>]
    let ``OUTCOME_WEIGHTS 100/0/0 always replies normally`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.SetBotSetting("OUTCOME_WEIGHTS", """{"reply":100,"silence":0,"emoji":0}""")
            do! fixture.ReloadSettings()
            do! fixture.SetAzureLlmScript [| LlmTestHelpers.scripted 200 (LlmTestHelpers.completionBody "привет") |]

            let update = mentionUpdate 6001L "outcome_reply" "здорово"
            let msgId = update.Message.Value.MessageId
            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! replies = botReplyRows msgId
            Assert.Single(replies) |> ignore

            let! reactions = fixture.GetFakeCalls("setMessageReaction")
            Assert.Empty(reactions)
        }

    [<Fact>]
    let ``OUTCOME_WEIGHTS 0/100/0 stays silent — no reply, no LLM call, no reaction`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.SetBotSetting("OUTCOME_WEIGHTS", """{"reply":0,"silence":100,"emoji":0}""")
            do! fixture.ReloadSettings()
            do! fixture.SetAzureLlmScript [||]

            let update = mentionUpdate 6002L "outcome_silence" "здорово"
            let msgId = update.Message.Value.MessageId
            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            // Prove absence, not just "not yet": wait out a generous window.
            do! Task.Delay 1500

            let! replies = botReplyRows msgId
            Assert.Empty(replies)
            let! sends = fixture.GetFakeCalls("sendMessage")
            Assert.Empty(sends |> Array.filter (isToChat fixture.TargetChatId))
            let! reactions = fixture.GetFakeCalls("setMessageReaction")
            Assert.Empty(reactions)
            let! llmCalls = fixture.GetAzureLlmCalls()
            Assert.Empty(llmCalls)
        }

    [<Fact>]
    let ``OUTCOME_WEIGHTS 0/0/100 reacts with an emoji instead of replying`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()
            do! fixture.SetBotSetting("OUTCOME_WEIGHTS", """{"reply":0,"silence":0,"emoji":100}""")
            do! fixture.ReloadSettings()
            do! fixture.SetAzureLlmScript [| scripted 200 (nonStreamCompletionBody "🔥") |]

            let update = mentionUpdate 6003L "outcome_emoji" "здорово"
            let msgId = update.Message.Value.MessageId
            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! replies = botReplyRows msgId
            Assert.Empty(replies)

            let deadline = DateTime.UtcNow + TimeSpan.FromSeconds 5.
            let mutable reactions = [||]
            while reactions.Length = 0 && DateTime.UtcNow < deadline do
                let! r = fixture.GetFakeCalls("setMessageReaction")
                reactions <- r
                if reactions.Length = 0 then do! Task.Delay 150

            Assert.NotEmpty(reactions)
            // Parse, don't raw-substring — System.Text.Json escapes non-ASCII (incl. this
            // emoji's surrogate pair) as \uXXXX by default (see AGENTS.md's "Russian text
            // in tests" rule, same reasoning applies to any non-ASCII wire content).
            use doc = JsonDocument.Parse(reactions[0].Body: string)
            Assert.Equal(int64 msgId, doc.RootElement.GetProperty("message_id").GetInt64())
            let reactionArr = doc.RootElement.GetProperty("reaction")
            Assert.Equal(1, reactionArr.GetArrayLength())
            Assert.Equal("🔥", reactionArr[0].GetProperty("emoji").GetString())
        }

    // ── Reaction palette / choice mode ───────────────────────────────────────

    [<Fact>]
    let ``REACTION_CHOICE_MODE=random picks only from REACTION_PALETTE, never calls the LLM`` () =
        task {
            try
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()
                do! fixture.SetBotSetting("OUTCOME_WEIGHTS", """{"reply":0,"silence":0,"emoji":100}""")
                do! fixture.SetBotSetting("REACTION_CHOICE_MODE", "random")
                do! fixture.SetBotSetting("REACTION_PALETTE", """["🎉"]""")
                do! fixture.ReloadSettings()
                do! fixture.SetAzureLlmScript [||]

                let update = mentionUpdate 6011L "outcome_palette_random" "здорово"
                let msgId = update.Message.Value.MessageId
                let! resp = fixture.SendUpdate(update)
                resp.EnsureSuccessStatusCode() |> ignore

                let! reactions = awaitReactions 5000
                Assert.NotEmpty(reactions)
                Assert.Equal("🎉", reactedEmoji reactions)

                // random mode never touches the LLM at all.
                let! llmCalls = fixture.GetAzureLlmCalls()
                Assert.Empty(llmCalls)
                ignore msgId
            finally
                fixture.SetBotSetting("REACTION_CHOICE_MODE", "llm") |> ignore
                fixture.SetBotSetting("REACTION_PALETTE", """["👍","❤","🔥","😁","🤔","🤯","😱","🤬","😢","🎉","🤩","💩","🤡","🥱"]""") |> ignore
                fixture.ReloadSettings() |> ignore
        }

    [<Fact>]
    let ``REACTION_CHOICE_MODE=llm picks the scripted emoji from REACTION_PALETTE`` () =
        task {
            try
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()
                do! fixture.SetBotSetting("OUTCOME_WEIGHTS", """{"reply":0,"silence":0,"emoji":100}""")
                do! fixture.SetBotSetting("REACTION_CHOICE_MODE", "llm")
                do! fixture.SetBotSetting("REACTION_PALETTE", """["🎉","💯"]""")
                do! fixture.ReloadSettings()
                do! fixture.SetAzureLlmScript [| scripted 200 (nonStreamCompletionBody "💯") |]

                let update = mentionUpdate 6012L "outcome_palette_llm" "здорово"
                let msgId = update.Message.Value.MessageId
                let! resp = fixture.SendUpdate(update)
                resp.EnsureSuccessStatusCode() |> ignore

                let! reactions = awaitReactions 5000
                Assert.NotEmpty(reactions)
                Assert.Equal("💯", reactedEmoji reactions)

                let! llmCalls = fixture.GetAzureLlmCalls()
                Assert.NotEmpty(llmCalls)
                ignore msgId
            finally
                fixture.SetBotSetting("REACTION_CHOICE_MODE", "llm") |> ignore
                fixture.SetBotSetting("REACTION_PALETTE", """["👍","❤","🔥","😁","🤔","🤯","😱","🤬","😢","🎉","🤩","💩","🤡","🥱"]""") |> ignore
                fixture.ReloadSettings() |> ignore
        }

    [<Fact>]
    let ``a failed emoji-pick LLM call falls back to a random pick from the palette`` () =
        task {
            try
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()
                do! fixture.SetBotSetting("OUTCOME_WEIGHTS", """{"reply":0,"silence":0,"emoji":100}""")
                do! fixture.SetBotSetting("REACTION_CHOICE_MODE", "llm")
                // A single-entry palette makes the random fallback's outcome deterministic —
                // this test is about "did it fall back at all", not "is Random.Shared fair".
                do! fixture.SetBotSetting("REACTION_PALETTE", """["🎉"]""")
                do! fixture.ReloadSettings()
                do! fixture.SetAzureLlmScript
                        [| scripted 400 """{"error":{"code":"content_filter","message":"blocked"}}""" |]

                let update = mentionUpdate 6013L "outcome_palette_fallback" "здорово"
                let msgId = update.Message.Value.MessageId
                let! resp = fixture.SendUpdate(update)
                resp.EnsureSuccessStatusCode() |> ignore

                let! reactions = awaitReactions 5000
                Assert.NotEmpty(reactions)
                Assert.Equal("🎉", reactedEmoji reactions)

                let! logs = fixture.GetBotLogs()
                Assert.Contains("falling back to a random pick", logs)
                ignore msgId
            finally
                fixture.SetBotSetting("REACTION_CHOICE_MODE", "llm") |> ignore
                fixture.SetBotSetting("REACTION_PALETTE", """["👍","❤","🔥","😁","🤔","🤯","😱","🤬","😢","🎉","🤩","💩","🤡","🥱"]""") |> ignore
                fixture.ReloadSettings() |> ignore
        }

    [<Fact>]
    let ``REACTION_PALETTE entries outside Telegram's allowed set are dropped with a Warning`` () =
        task {
            try
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()
                do! fixture.SetBotSetting("OUTCOME_WEIGHTS", """{"reply":0,"silence":0,"emoji":100}""")
                do! fixture.SetBotSetting("REACTION_CHOICE_MODE", "random")
                // "🚀" is not on Telegram's documented setMessageReaction allowed-emoji list
                // (OutcomeRouter.telegramAllowedReactionEmoji) — it must be filtered out,
                // leaving "🎉" as the only (deterministic) pick.
                do! fixture.SetBotSetting("REACTION_PALETTE", """["🎉","🚀"]""")
                do! fixture.ReloadSettings()
                do! fixture.SetAzureLlmScript [||]

                let update = mentionUpdate 6014L "outcome_palette_invalid" "здорово"
                let msgId = update.Message.Value.MessageId
                let! resp = fixture.SendUpdate(update)
                resp.EnsureSuccessStatusCode() |> ignore

                let! reactions = awaitReactions 5000
                Assert.NotEmpty(reactions)
                Assert.Equal("🎉", reactedEmoji reactions)

                let! logs = fixture.GetBotLogs()
                Assert.Contains("REACTION_PALETTE", logs)
                Assert.Contains("dropped", logs)
                ignore msgId
            finally
                fixture.SetBotSetting("REACTION_CHOICE_MODE", "llm") |> ignore
                fixture.SetBotSetting("REACTION_PALETTE", """["👍","❤","🔥","😁","🤔","🤯","😱","🤬","😢","🎉","🤩","💩","🤡","🥱"]""") |> ignore
                fixture.ReloadSettings() |> ignore
        }

    // ── MarkdownV2 final-message rendering ──────────────────────────────────

    [<Fact>]
    let ``final reply is delivered as MarkdownV2 with escaped payload`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.SetBotSetting("OUTCOME_WEIGHTS", """{"reply":100,"silence":0,"emoji":0}""")
            do! fixture.ReloadSettings()
            do! fixture.SetAzureLlmScript [| LlmTestHelpers.scripted 200 (LlmTestHelpers.completionBody "**жирный** и `код`") |]

            let update = mentionUpdate 6101L "mdv2_alice" "покажи форматирование"
            let msgId = update.Message.Value.MessageId
            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! edits = fixture.GetFakeCalls("editMessageText")
            let final =
                edits
                |> Array.tryFind (fun c -> jsonString c "text" = "*жирный* и `код`")
            Assert.True(final.IsSome, "expected the final editMessageText to carry the MDV2-escaped text")
            Assert.Equal("MarkdownV2", jsonString final.Value "parse_mode")

            // message_log keeps the plain, unescaped text.
            let! replies = botReplyRows msgId
            Assert.Single(replies) |> ignore
            Assert.Equal("**жирный** и `код`", replies[0].text)
        }

    [<Fact>]
    let ``a Telegram 400 on the MDV2 edit falls back to a plain-text edit`` () =
        task {
            try
                do! fixture.ClearFakeCalls()
                do! fixture.SetBotSetting("OUTCOME_WEIGHTS", """{"reply":100,"silence":0,"emoji":0}""")
                do! fixture.ReloadSettings()
                do! fixture.SetMdv2Rejected(true)
                do! fixture.SetAzureLlmScript [| LlmTestHelpers.scripted 200 (LlmTestHelpers.completionBody "**жирный** текст") |]

                let update = mentionUpdate 6102L "mdv2_bob" "покажи форматирование"
                let msgId = update.Message.Value.MessageId
                let! resp = fixture.SendUpdate(update)
                resp.EnsureSuccessStatusCode() |> ignore

                let! edits = fixture.GetFakeCalls("editMessageText")
                let plainFinal = edits |> Array.tryFind (fun c -> jsonString c "text" = "**жирный** текст")
                Assert.True(plainFinal.IsSome, "expected a plain-text fallback edit carrying the original unescaped text")

                let! replies = botReplyRows msgId
                Assert.Single(replies) |> ignore

                let! logs = fixture.GetBotLogs()
                Assert.Contains("MDV2 editMessageText rejected", logs)
            finally
                fixture.SetMdv2Rejected(false) |> ignore
        }

    // ── Rewriter pass ─────────────────────────────────────────────────────────

    [<Fact>]
    let ``REWRITER_ENABLED=true rewrites the final text via a second non-stream call`` () =
        task {
            try
                do! fixture.ClearFakeCalls()
                do! fixture.SetBotSetting("OUTCOME_WEIGHTS", """{"reply":100,"silence":0,"emoji":0}""")
                do! fixture.SetBotSetting("REWRITER_ENABLED", "true")
                do! fixture.ReloadSettings()
                do! fixture.SetAzureLlmScript
                        [| scripted 200 (nonStreamCompletionBody "исходный ответ ассистента")
                           scripted 200 (nonStreamCompletionBody "переписанный живой ответ") |]

                let update = mentionUpdate 6201L "rewriter_carl" "перескажи новости"
                let msgId = update.Message.Value.MessageId
                let! resp = fixture.SendUpdate(update)
                resp.EnsureSuccessStatusCode() |> ignore

                let! replies = botReplyRows msgId
                Assert.Single(replies) |> ignore
                Assert.Equal("переписанный живой ответ", replies[0].text)

                let! llmCalls = fixture.GetAzureLlmCalls()
                Assert.Equal(2, llmCalls.Length)
            finally
                fixture.SetBotSetting("REWRITER_ENABLED", "false") |> ignore
                fixture.ReloadSettings() |> ignore
        }

    // ── Reply-context enrichment ─────────────────────────────────────────────

    [<Fact>]
    let ``a reply-to-message is quoted (author + text) into the LLM request`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.SetBotSetting("OUTCOME_WEIGHTS", """{"reply":100,"silence":0,"emoji":0}""")
            do! fixture.ReloadSettings()

            let quotedAuthor = Tg.user(id = 6301L, username = "quoted_dave", firstName = "Dave")
            let quotedText = "уникальный_маркер_кит текст который цитируется"
            let quotedMsg =
                (Tg.groupMessage(quotedText, quotedAuthor, fixture.TargetChatId)).Message.Value

            let asker = Tg.user(id = 6302L, username = "asker_erin", firstName = "Erin")
            let triggerUpdate =
                Tg.groupMessage("алита что скажешь на это?", asker, fixture.TargetChatId, replyToMessage = quotedMsg)

            do! fixture.SetAzureLlmScript [| LlmTestHelpers.scripted 200 (LlmTestHelpers.completionBody "ок") |]
            let! resp = fixture.SendUpdate(triggerUpdate)
            resp.EnsureSuccessStatusCode() |> ignore

            let! llmCalls = fixture.GetAzureLlmCalls()
            Assert.True(
                llmCalls |> Array.exists (fun c -> requestMessagesContain c quotedText),
                "expected the replied-to message's text to be quoted into the LLM request")
            Assert.True(
                llmCalls |> Array.exists (fun c -> requestMessagesContain c "Dave"),
                "expected the replied-to message's author to be attributed in the LLM request")
        }

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            ValueTask(task {
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()
                do! fixture.SetAzureLlmScript [||]
                do! fixture.SetMdv2Rejected(false)
                do! fixture.SetBotSetting("RESPONDER_MODE", "llm")
                do! fixture.SetBotSetting("STREAM_MODE", "edit")
                do! fixture.SetBotSetting("OUTCOME_WEIGHTS", """{"reply":100,"silence":0,"emoji":0}""")
                do! fixture.SetBotSetting("REWRITER_ENABLED", "false")
                do! fixture.SetBotSetting("REACTION_PALETTE", """["👍","❤","🔥","😁","🤔","🤯","😱","🤬","😢","🎉","🤩","💩","🤡","🥱"]""")
                do! fixture.SetBotSetting("REACTION_CHOICE_MODE", "llm")
                do! fixture.ReloadSettings()
            } :> Task)

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(task {
                do! fixture.SetAzureLlmScript [||]
                do! fixture.SetMdv2Rejected(false)
                do! fixture.SetBotSetting("RESPONDER_MODE", "echo")
                do! fixture.SetBotSetting("OUTCOME_WEIGHTS", """{"reply":100,"silence":0,"emoji":0}""")
                do! fixture.SetBotSetting("REWRITER_ENABLED", "false")
                do! fixture.SetBotSetting("REACTION_PALETTE", """["👍","❤","🔥","😁","🤔","🤯","😱","🤬","😢","🎉","🤩","💩","🤡","🥱"]""")
                do! fixture.SetBotSetting("REACTION_CHOICE_MODE", "llm")
                do! fixture.ReloadSettings()
            } :> Task)
