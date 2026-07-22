namespace AlitaBot.Tests

open System
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open BotTestInfra
open Funogram.Telegram.Types
open Xunit

open CommandsTestHelpers

/// S10 PR1: natural-language tool-calling loop (generate_image, web_search) — fake-suite
/// coverage of AgentToolLoop/ToolExecutor/ToolRegistry/AzureResponsesWebSearch against the
/// fake Azure chat-completions (SSE) + Responses API backends. Every test flips
/// NL_TOOLS_ENABLED (and RESPONDER_MODE) for its duration via IAsyncLifetime, mirroring
/// LlmTests.fs's convention — the shared fixture's baseline keeps both off/echo so every
/// OTHER RESPONDER_MODE=llm test suite keeps its pre-S10 request/response shape.
module NlToolLoopTestHelpers =
    /// Builds a scripted `message.tool_calls`-shaped chat-completion JSON (OpenAI
    /// non-stream shape, `id`/`type`/`function.name`/`function.arguments`, no `index` — the
    /// fake's `respondChatCompletionSse` rebuilds the SSE delta shape with `index` added).
    /// `finish_reason` is "tool_calls".
    let toolCallsCompletionBody (calls: (string * string * string) list) : string =
        let root = JsonObject()
        root["id"] <- JsonValue.Create "chatcmpl-fake-tools"
        root["object"] <- JsonValue.Create "chat.completion"
        root["model"] <- JsonValue.Create "gpt-5-mini-2025-08-07"
        let toolCalls = JsonArray()
        for (id, name, args) in calls do
            let c = JsonObject()
            c["id"] <- JsonValue.Create id
            c["type"] <- JsonValue.Create "function"
            let f = JsonObject()
            f["name"] <- JsonValue.Create name
            f["arguments"] <- JsonValue.Create args
            c["function"] <- f
            toolCalls.Add c
        let message = JsonObject()
        message["role"] <- JsonValue.Create "assistant"
        message["tool_calls"] <- toolCalls
        let choice = JsonObject()
        choice["index"] <- JsonValue.Create 0
        choice["finish_reason"] <- JsonValue.Create "tool_calls"
        choice["message"] <- message
        let choices = JsonArray()
        choices.Add choice
        root["choices"] <- choices
        let usage = JsonObject()
        usage["prompt_tokens"] <- JsonValue.Create 150
        usage["completion_tokens"] <- JsonValue.Create 30
        usage["total_tokens"] <- JsonValue.Create 180
        root["usage"] <- usage
        root.ToJsonString()

    /// Builds a scripted Azure Responses API body (`AzureResponsesWire.tryParseResponsesOutput`'s
    /// expected shape) with a single `output_text` message and no citations.
    let azureResponsesBody (text: string) : string =
        let root = JsonObject()
        let outputTextPart = JsonObject()
        outputTextPart["type"] <- JsonValue.Create "output_text"
        outputTextPart["text"] <- JsonValue.Create text
        outputTextPart["annotations"] <- JsonArray()
        let content = JsonArray()
        content.Add outputTextPart
        let message = JsonObject()
        message["type"] <- JsonValue.Create "message"
        message["status"] <- JsonValue.Create "completed"
        message["role"] <- JsonValue.Create "assistant"
        message["content"] <- content
        let output = JsonArray()
        output.Add message
        root["output"] <- output
        let usage = JsonObject()
        usage["input_tokens"] <- JsonValue.Create 10
        usage["output_tokens"] <- JsonValue.Create 5
        usage["total_tokens"] <- JsonValue.Create 15
        root["usage"] <- usage
        root.ToJsonString()

    /// Tool names offered in a chat-completions request's `tools[].function.name`.
    let requestToolNames (call: FakeCall) : string list =
        use doc = JsonDocument.Parse(call.Body)
        match doc.RootElement.TryGetProperty "tools" with
        | true, tools when tools.ValueKind = JsonValueKind.Array ->
            [ for t in tools.EnumerateArray() do
                match t.TryGetProperty "function" with
                | true, f ->
                    match f.TryGetProperty "name" with
                    | true, n when n.ValueKind = JsonValueKind.String -> n.GetString()
                    | _ -> ()
                | _ -> () ]
        | _ -> []

    let requestHasToolsKey (call: FakeCall) : bool =
        use doc = JsonDocument.Parse(call.Body)
        match doc.RootElement.TryGetProperty "tools" with
        | true, _ -> true
        | _ -> false

    /// True when `call`'s `messages[]` contains a `role="tool"` entry with the given
    /// `tool_call_id`.
    let requestHasToolResultFor (call: FakeCall) (toolCallId: string) : bool =
        use doc = JsonDocument.Parse(call.Body)
        match doc.RootElement.TryGetProperty "messages" with
        | true, messages when messages.ValueKind = JsonValueKind.Array ->
            messages.EnumerateArray()
            |> Seq.exists (fun m ->
                let role = match m.TryGetProperty "role" with true, r when r.ValueKind = JsonValueKind.String -> r.GetString() | _ -> ""
                let tcid = match m.TryGetProperty "tool_call_id" with true, t when t.ValueKind = JsonValueKind.String -> t.GetString() | _ -> ""
                role = "tool" && tcid = toolCallId)
        | _ -> false

type NlToolLoopTests(fixture: AlitaTestContainers) =

    let botReplyRow (replyToMessageId: int64) =
        fixture.QuerySingleOrDefault<MessageLogRow>(
            """
SELECT chat_id, message_id, user_id, username, display_name, is_bot, reply_to_message_id, text
FROM message_log WHERE chat_id = @cid AND is_bot = TRUE AND reply_to_message_id = @mid
""",
            {| cid = fixture.TargetChatId; mid = replyToMessageId |})

    let botReplyRows (replyToMessageId: int64) =
        fixture.Query<MessageLogRow>(
            """
SELECT chat_id, message_id, user_id, username, display_name, is_bot, reply_to_message_id, text
FROM message_log WHERE chat_id = @cid AND is_bot = TRUE AND reply_to_message_id = @mid
ORDER BY message_id
""",
            {| cid = fixture.TargetChatId; mid = replyToMessageId |})

    [<Fact>]
    let ``NL_TOOLS_ENABLED=false sends the pre-S10 request shape with no tools key`` () =
        task {
            do! fixture.SetBotSetting("NL_TOOLS_ENABLED", "false")
            do! fixture.ReloadSettings()
            do! fixture.SetAzureLlmScript [| LlmTestHelpers.scripted 200 (LlmTestHelpers.completionBody "просто ответ") |]

            let user = Tg.user(id = 9501L, username = "nl_alice", firstName = "Alice")
            let update = Tg.groupMessage("алита привет", user, fixture.TargetChatId)
            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! llmCalls = fixture.GetAzureLlmCalls()
            Assert.Single(llmCalls) |> ignore
            Assert.False(NlToolLoopTestHelpers.requestHasToolsKey llmCalls[0])
        }

    [<Fact>]
    let ``NL tools enabled but no tool needed sends one normal streamed reply with both tools offered`` () =
        task {
            do! fixture.SetAzureLlmScript [| LlmTestHelpers.scripted 200 (LlmTestHelpers.completionBody "привет, как сама?") |]

            let user = Tg.user(id = 9502L, username = "nl_bob", firstName = "Bob")
            let update = Tg.groupMessage("алита привет", user, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId
            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! llmCalls = fixture.GetAzureLlmCalls()
            Assert.Single(llmCalls) |> ignore
            let toolNames = NlToolLoopTestHelpers.requestToolNames llmCalls[0]
            Assert.Contains("generate_image", toolNames)
            Assert.Contains("web_search", toolNames)

            let! row = botReplyRow msgId
            Assert.NotNull(box row)
            Assert.Equal("привет, как сама?", row.text)
        }

    [<Fact>]
    let ``NL image ask runs a tool-call round then sends a photo captioned with the composed caption, not the raw prompt`` () =
        task {
            let scriptedCaption = "вот, специально для тебя"
            do! fixture.SetAzureLlmScript
                    [| LlmTestHelpers.scripted
                           200
                           (NlToolLoopTestHelpers.toolCallsCompletionBody
                               [ "call_1", "generate_image", """{"prompt":"рыжий кот на подоконнике"}""" ])
                       LlmTestHelpers.scripted 200 (nonStreamCompletionBody scriptedCaption)
                       LlmTestHelpers.scripted 200 (LlmTestHelpers.completionBody "любуйся") |]

            let user = Tg.user(id = 9503L, username = "nl_carol", firstName = "Carol")
            let update = Tg.groupMessage("алита, нарисуй рыжего кота на подоконнике", user, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId
            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! photoSends = fixture.GetFakeCalls("sendPhoto")
            let toChat = photoSends |> Array.filter (isToChat fixture.TargetChatId)
            Assert.NotEmpty toChat
            Assert.Contains(toChat, fun c -> (jsonString c "caption") = scriptedCaption)
            Assert.DoesNotContain(toChat, fun c -> (jsonString c "caption").Contains "рыжего кота")

            let! replies = botReplyRows msgId
            Assert.Contains(replies, fun (r: MessageLogRow) -> r.text = $"[image] {scriptedCaption}")
        }

    [<Fact>]
    let ``tool result is fed back to the model as a role=tool message with the matching tool_call_id`` () =
        task {
            do! fixture.SetAzureLlmScript
                    [| LlmTestHelpers.scripted
                           200
                           (NlToolLoopTestHelpers.toolCallsCompletionBody
                               [ "call_abc", "generate_image", """{"prompt":"тестовый кот"}""" ])
                       LlmTestHelpers.scripted 200 (nonStreamCompletionBody "готово")
                       LlmTestHelpers.scripted 200 (LlmTestHelpers.completionBody "вот и всё") |]

            let user = Tg.user(id = 9504L, username = "nl_dave", firstName = "Dave")
            let update = Tg.groupMessage("алита, нарисуй тестового кота", user, fixture.TargetChatId)
            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! llmCalls = fixture.GetAzureLlmCalls()
            Assert.Equal(3, llmCalls.Length)
            Assert.True(
                NlToolLoopTestHelpers.requestHasToolResultFor llmCalls[2] "call_abc",
                "expected round 2's request to include a role=tool message with tool_call_id=call_abc")
        }

    [<Fact>]
    let ``web_search calls the Responses API with the query and threads the grounded text into the next round`` () =
        task {
            let groundedMarker = "DOTNET10RELEASEMARKER"
            do! fixture.SetAzureResponsesScript
                    [| LlmTestHelpers.scripted 200 (NlToolLoopTestHelpers.azureResponsesBody $"{groundedMarker} released 2025-11-11") |]
            do! fixture.SetAzureLlmScript
                    [| LlmTestHelpers.scripted
                           200
                           (NlToolLoopTestHelpers.toolCallsCompletionBody
                               [ "call_ws1", "web_search", """{"query":"когда вышел .NET 10"}""" ])
                       LlmTestHelpers.scripted 200 (LlmTestHelpers.completionBody "нашла, вышел недавно") |]

            let user = Tg.user(id = 9505L, username = "nl_erin", firstName = "Erin")
            let update = Tg.groupMessage("алита, найди в интернете когда вышел .NET 10", user, fixture.TargetChatId)
            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! responsesCalls = fixture.GetAzureResponsesCalls()
            Assert.Single(responsesCalls) |> ignore
            use doc = JsonDocument.Parse(responsesCalls[0].Body)
            let toolsEl = doc.RootElement.GetProperty "tools"
            Assert.Equal("web_search", toolsEl[0].GetProperty("type").GetString())
            Assert.Contains("NET 10", doc.RootElement.GetProperty("input").GetString())

            let! llmCalls = fixture.GetAzureLlmCalls()
            Assert.Equal(2, llmCalls.Length)
            Assert.True(requestMessagesContain llmCalls[1] groundedMarker)
        }

    [<Fact>]
    let ``iteration cap stops the loop and strips tools from the final request`` () =
        task {
            // NL_TOOLS_MAX_ITERATIONS=4 (fixture seed) — script 5 tool_calls rounds (all
            // web_search, so tool execution never fires a nested chat-completions call the
            // way generate_image's composeCaption would); iteration 5 exceeds the cap, so
            // its request carries no tools regardless of what the scripted response looks
            // like — see AgentToolLoop.Run's doc comment.
            let toolCallsRound =
                LlmTestHelpers.scripted
                    200
                    (NlToolLoopTestHelpers.toolCallsCompletionBody [ "call_n", "web_search", """{"query":"тест"}""" ])
            do! fixture.SetAzureLlmScript (Array.create 5 toolCallsRound)
            do! fixture.SetAzureResponsesScript [| LlmTestHelpers.scripted 200 (NlToolLoopTestHelpers.azureResponsesBody "результат поиска") |]

            let user = Tg.user(id = 9507L, username = "nl_frank", firstName = "Frank")
            let update = Tg.groupMessage("алита, найди тест", user, fixture.TargetChatId)
            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! llmCalls = fixture.GetAzureLlmCalls()
            Assert.Equal(5, llmCalls.Length)
            Assert.False(NlToolLoopTestHelpers.requestHasToolsKey llmCalls[4])
        }

    [<Fact>]
    let ``malformed generate_image arguments produce a graceful bad_arguments tool message and the turn still completes`` () =
        task {
            do! fixture.SetAzureLlmScript
                    [| LlmTestHelpers.scripted 200 (NlToolLoopTestHelpers.toolCallsCompletionBody [ "call_bad", "generate_image", "{}" ])
                       LlmTestHelpers.scripted 200 (LlmTestHelpers.completionBody "ладно, скажи ещё раз, что нарисовать") |]

            let user = Tg.user(id = 9508L, username = "nl_grace", firstName = "Grace")
            let update = Tg.groupMessage("алита, нарисуй", user, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId
            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! photoSends = fixture.GetFakeCalls("sendPhoto")
            Assert.Empty(photoSends |> Array.filter (isToChat fixture.TargetChatId))

            let! llmCalls = fixture.GetAzureLlmCalls()
            Assert.Equal(2, llmCalls.Length)

            let! row = botReplyRow msgId
            Assert.NotNull(box row)
            Assert.Equal("ладно, скажи ещё раз, что нарисовать", row.text)
        }

    [<Fact>]
    let ``generate_image is denied once the per-user hourly rate limit is hit, with no image call fired`` () =
        task {
            let user = Tg.user(id = 9509L, username = "nl_henry", firstName = "Henry")
            let now = DateTime.UtcNow
            for _ in 1 .. 20 do
                do! insertUsageRow fixture now "image" "gpt-5-mini" 0.01 fixture.TargetChatId user.Id

            do! fixture.SetAzureLlmScript
                    [| LlmTestHelpers.scripted 200 (NlToolLoopTestHelpers.toolCallsCompletionBody [ "call_rl", "generate_image", """{"prompt":"кот"}""" ])
                       LlmTestHelpers.scripted 200 (LlmTestHelpers.completionBody "притормози, попозже") |]

            let update = Tg.groupMessage("алита, нарисуй кота", user, fixture.TargetChatId)
            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! azureCalls = fixture.GetAzureOcrCalls()
            Assert.DoesNotContain(
                azureCalls,
                fun (c: FakeCall) -> c.Url.Contains "images/generations" || c.Url.Contains "images/edits")

            let! llmCalls = fixture.GetAzureLlmCalls()
            Assert.Equal(2, llmCalls.Length)
        }

    [<Fact>]
    let ``executor throwing (a Telegram API failure inside the tool) still completes the turn with a sent message`` () =
        task {
            try
                // Three scripted LLM calls, in order: the tool_calls round, then
                // MediaActions.composeCaption's non-stream call (reached BEFORE the
                // sendPhotoReply that actually throws — the caption text itself is
                // irrelevant here since the photo never sends), then the final round the
                // loop falls through to once AgentToolLoop's try/with turns the thrown
                // TelegramApiException into a "tool_exception" Tool-role message.
                do! fixture.SetAzureLlmScript
                        [| LlmTestHelpers.scripted 200 (NlToolLoopTestHelpers.toolCallsCompletionBody [ "call_boom", "generate_image", """{"prompt":"кот"}""" ])
                           LlmTestHelpers.scripted 200 (nonStreamCompletionBody "неважно")
                           LlmTestHelpers.scripted 200 (LlmTestHelpers.completionBody "ну не вышло, ладно") |]
                do! fixture.SetMethodError("sendPhoto", true)

                let user = Tg.user(id = 9510L, username = "nl_ivan", firstName = "Ivan")
                let update = Tg.groupMessage("алита, нарисуй кота", user, fixture.TargetChatId)
                let msgId = update.Message.Value.MessageId
                let! resp = fixture.SendUpdate(update)
                resp.EnsureSuccessStatusCode() |> ignore

                let! row = botReplyRow msgId
                Assert.NotNull(box row)
                Assert.Equal("ну не вышло, ладно", row.text)

                let! logs = fixture.GetBotLogs()
                Assert.Contains("threw", logs)
            finally
                fixture.SetMethodError("sendPhoto", false) |> ignore
        }

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            ValueTask(task {
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()
                do! fixture.SetAzureLlmScript [||]
                do! fixture.SetAzureImageScript [||]
                do! fixture.SetAzureResponsesScript [||]
                do! fixture.SetAzureLlmStreamOptions(0, 0, 0)
                do! fixture.SetBotSetting("RESPONDER_MODE", "llm")
                do! fixture.SetBotSetting("NL_TOOLS_ENABLED", "true")
                do! fixture.ReloadSettings()
            } :> Task)

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(task {
                do! fixture.SetAzureLlmScript [||]
                do! fixture.SetAzureImageScript [||]
                do! fixture.SetAzureResponsesScript [||]
                do! fixture.SetAzureLlmStreamOptions(0, 0, 0)
                do! fixture.SetBotSetting("RESPONDER_MODE", "echo")
                do! fixture.SetBotSetting("NL_TOOLS_ENABLED", "false")
                do! fixture.ReloadSettings()
            } :> Task)
