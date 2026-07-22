namespace AlitaBot.Tests

open System
open System.Text.Json
open System.Threading.Tasks
open BotTestInfra
open Funogram.Telegram.Types
open Xunit

module LlmTestHelpers =
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

    /// Full chat-completion JSON the fake either returns as-is (non-stream) or
    /// splits into ~3 SSE chunks (stream). Model matches the seeded LLM_PRICING key.
    let completionBody (content: string) =
        let contentJson = JsonSerializer.Serialize(content)
        $"""{{"id":"chatcmpl-alita","object":"chat.completion","model":"gpt-5-mini-2025-08-07","choices":[{{"index":0,"finish_reason":"stop","message":{{"role":"assistant","content":{contentJson}}}}}],"usage":{{"prompt_tokens":120,"completion_tokens":42,"total_tokens":162}}}}"""

    let scripted (status: int) (body: string) : AzureScriptedResponse =
        { status = status; body = body; delayMs = 0; errorMode = "" }

    /// Slice 6: renderers apply MarkdownV2 formatting to the FINAL delivered message —
    /// mirrors `MarkdownRenderer.escapeText`'s reserved-char list for a plain (no active
    /// markdown syntax) single-paragraph string, which is all these scripted replies ever
    /// are. Kept independent of production code on purpose (same posture as every other
    /// hand-built "expected wire value" in this test class).
    let escapeMdv2Plain (s: string) =
        let reserved =
            set [ '\\'; '_'; '*'; '['; ']'; '('; ')'; '~'; '`'; '>'; '#'; '+'; '-'; '='; '|'; '{'; '}'; '.'; '!' ]
        s |> Seq.map (fun c -> if reserved.Contains c then $"\\{c}" else string c) |> String.concat ""

/// LLM responder mode tests against the fake Azure chat-completions (SSE) backend.
/// Each test flips RESPONDER_MODE to llm for its duration and restores echo after,
/// so the M1 skeleton tests keep their expected mode regardless of ordering.
type LlmTests(fixture: AlitaTestContainers) =

    let scriptedContent =
        "Привет! Я Алита. Этот стримовый тестовый ответ специально сделан достаточно длинным, чтобы фейк разбил его на несколько SSE-кусков."

    let mentionUpdate (userId: int64) (username: string) (tail: string) =
        let user = Tg.user(id = userId, username = username, firstName = username)
        let mention = $"@{fixture.BotUsername}"
        let text = $"{mention} {tail}"
        let entities = [| MessageEntity.Create(``type`` = "mention", offset = 0L, length = int64 mention.Length) |]
        Tg.quickMsg(text = text, chat = Tg.chat(id = fixture.TargetChatId), from = user, entities = entities)

    let botReplyRow (replyToMessageId: int64) =
        fixture.QuerySingleOrDefault<MessageLogRow>(
            """
SELECT chat_id, message_id, user_id, username, display_name, is_bot, reply_to_message_id, text
FROM message_log WHERE chat_id = @cid AND is_bot = TRUE AND reply_to_message_id = @mid
""",
            {| cid = fixture.TargetChatId; mid = replyToMessageId |})

    [<Fact>]
    let ``Mention streams a scripted reply via editMessageText and logs the full text`` () =
        task {
            do! fixture.SetAzureLlmScript [| LlmTestHelpers.scripted 200 (LlmTestHelpers.completionBody scriptedContent) |]
            let update = mentionUpdate 4001L "llm_alice" "расскажи что-нибудь"
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            // First meaningful chunk lands as a sendMessage with a proper prefix…
            let! sends = fixture.GetFakeCalls("sendMessage")
            let firstChunkSend =
                sends
                |> Array.filter (LlmTestHelpers.isToChat fixture.TargetChatId)
                |> Array.filter (fun c ->
                    let text = LlmTestHelpers.jsonString c "text"
                    text.Length > 0 && scriptedContent.StartsWith(text, StringComparison.Ordinal))
            Assert.NotEmpty(firstChunkSend)

            // …and the full text arrives via the editMessageText path, MarkdownV2-formatted
            // (Slice 6: the final edit always applies MDV2, even for a plain-sentence reply
            // with no markdown syntax of its own — only its reserved characters change).
            let! edits = fixture.GetFakeCalls("editMessageText")
            let expected = LlmTestHelpers.escapeMdv2Plain scriptedContent
            Assert.True(
                edits
                |> Array.exists (fun c ->
                    LlmTestHelpers.jsonString c "text" = expected
                    && LlmTestHelpers.jsonString c "parse_mode" = "MarkdownV2"),
                "Expected a final editMessageText carrying the MDV2-escaped full scripted reply")

            let! row = botReplyRow msgId
            Assert.NotNull(box row)
            Assert.Equal(scriptedContent, row.text)
        }

    [<Fact>]
    let ``Scripted 429 keeps the bot silent with no retry and no crash`` () =
        task {
            do! fixture.SetAzureLlmStreamOptions(0, 0, 1) // Retry-After: 1 on the 429
            do! fixture.SetAzureLlmScript
                    [| LlmTestHelpers.scripted 429 """{"error":{"code":"429","message":"Requests are being throttled"}}""" |]
            let update = mentionUpdate 4002L "llm_bob" "ты тут?"
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            // Silence: no outgoing messages at all.
            let! sends = fixture.GetFakeCalls("sendMessage")
            Assert.Empty(sends)
            let! edits = fixture.GetFakeCalls("editMessageText")
            Assert.Empty(edits)

            // Streamed request → exactly one LLM call, never a mid-stream retry.
            let! llmCalls = fixture.GetAzureLlmCalls()
            Assert.Single(llmCalls) |> ignore

            // No bot reply row; the failure only left a Warning in the logs.
            let! row = botReplyRow msgId
            Assert.Null(box row)
            let! logs = fixture.GetBotLogs()
            Assert.Contains("staying silent", logs)
        }

    [<Fact>]
    let ``Scripted content_filter 400 produces the fixed RU shrug reply`` () =
        task {
            do! fixture.SetAzureLlmScript
                    [| LlmTestHelpers.scripted 400 """{"error":{"code":"content_filter","message":"The prompt was filtered"}}""" |]
            let update = mentionUpdate 4003L "llm_eve" "какая-то дичь"
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! sends = fixture.GetFakeCalls("sendMessage")
            let shrugSends =
                sends
                |> Array.filter (LlmTestHelpers.isToChat fixture.TargetChatId)
                |> Array.filter (fun c -> (LlmTestHelpers.jsonString c "text").Contains("фильтр офигел"))
            Assert.NotEmpty(shrugSends)

            let! row = botReplyRow msgId
            Assert.NotNull(box row)
            Assert.Contains("фильтр офигел", row.text)
        }

    [<Fact>]
    let ``Mid-stream abort finalizes the partial text already streamed`` () =
        task {
            // Abort the connection after 2 of ~3 content chunks; the 100ms gaps let
            // the already-flushed chunks reach the bot before the reset.
            do! fixture.SetAzureLlmStreamOptions(100, 2, 0)
            do! fixture.SetAzureLlmScript [| LlmTestHelpers.scripted 200 (LlmTestHelpers.completionBody scriptedContent) |]
            let update = mentionUpdate 4004L "llm_mallory" "продолжай"
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! row = botReplyRow msgId
            Assert.NotNull(box row)
            Assert.True(row.text.Length > 0, "expected partial text to be finalized")
            Assert.True(row.text.Length < scriptedContent.Length, "expected a PARTIAL text, got the full reply")
            Assert.StartsWith(row.text, scriptedContent)

            // The partial text was delivered (send of the first chunk + finalizing edit,
            // the latter MarkdownV2-formatted same as a graceful completion — Slice 6).
            let! edits = fixture.GetFakeCalls("editMessageText")
            let expected = LlmTestHelpers.escapeMdv2Plain row.text
            Assert.True(
                edits
                |> Array.exists (fun c ->
                    LlmTestHelpers.jsonString c "text" = expected
                    && LlmTestHelpers.jsonString c "parse_mode" = "MarkdownV2"),
                "Expected a finalizing editMessageText with the MDV2-escaped partial text")
        }

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            ValueTask(task {
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()
                do! fixture.SetAzureLlmScript [||]
                do! fixture.SetAzureLlmStreamOptions(0, 0, 0)
                do! fixture.SetBotSetting("RESPONDER_MODE", "llm")
                do! fixture.ReloadSettings()
            } :> Task)

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(task {
                do! fixture.SetAzureLlmScript [||]
                do! fixture.SetAzureLlmStreamOptions(0, 0, 0)
                do! fixture.SetBotSetting("RESPONDER_MODE", "echo")
                do! fixture.ReloadSettings()
            } :> Task)
