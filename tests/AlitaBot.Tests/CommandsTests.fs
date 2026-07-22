namespace AlitaBot.Tests

open System
open System.Text.Json
open System.Threading.Tasks
open BotTestInfra
open Dapper
open Funogram.Telegram.Types
open Npgsql
open Xunit

module CommandsTestHelpers =
    let jsonString (call: FakeCall) (property: string) =
        use doc = JsonDocument.Parse(call.Body)
        match doc.RootElement.TryGetProperty property with
        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() |> Option.ofObj |> Option.defaultValue ""
        | _ -> ""

    let jsonHasNumberProperty (call: FakeCall) (property: string) =
        use doc = JsonDocument.Parse(call.Body)
        match doc.RootElement.TryGetProperty property with
        | true, v -> v.ValueKind = JsonValueKind.Number
        | _ -> false

    let isToChat (chatId: int64) (call: FakeCall) =
        use doc = JsonDocument.Parse(call.Body)
        match doc.RootElement.TryGetProperty "chat_id" with
        | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt64() = chatId
        | _ -> false

    /// True when any `messages[].content` string in a chat-completions request body
    /// contains `needle`. System.Text.Json escapes non-ASCII (incl. Cyrillic) as \uXXXX
    /// by default, so this parses JSON and compares decoded strings — never raw-substring
    /// a request body containing Cyrillic (see AGENTS.md's "Russian text in tests" rule).
    let requestMessagesContain (call: FakeCall) (needle: string) =
        use doc = JsonDocument.Parse(call.Body)
        match doc.RootElement.TryGetProperty "messages" with
        | true, messages when messages.ValueKind = JsonValueKind.Array ->
            messages.EnumerateArray()
            |> Seq.exists (fun m ->
                match m.TryGetProperty "content" with
                | true, c when c.ValueKind = JsonValueKind.String -> c.GetString().Contains needle
                | _ -> false)
        | _ -> false

    /// Directly inserts an `llm_usage` row (bypassing the app) so /usage tests can seed
    /// deterministic totals/windows without depending on a real provider call.
    let insertUsageRow
        (fixture: AlitaTestContainers)
        (calledAt: DateTime)
        (kind: string)
        (model: string)
        (costUsd: float)
        (chatId: int64)
        (userId: int64)
        =
        task {
            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            do! conn.OpenAsync()
            //language=postgresql
            let sql =
                """
INSERT INTO llm_usage (called_at, kind, model, input_tokens, output_tokens, cost_usd, chat_id, user_id)
VALUES (@called_at, @kind, @model, 100, 50, @cost_usd, @chat_id, @user_id);
"""
            let! _ =
                conn.ExecuteAsync(
                    sql,
                    {| called_at = calledAt
                       kind = kind
                       model = model
                       cost_usd = costUsd
                       chat_id = chatId
                       user_id = userId |})
            return ()
        }

    /// Polls (up to 3s) for a matching `llm_usage` row — usage rows are written
    /// fire-and-forget (LlmTelemetry.fs) so they can land slightly after the reply does.
    let awaitUsageRow (fixture: AlitaTestContainers) (kind: string) (chatId: int64) =
        task {
            let deadline = DateTime.UtcNow + TimeSpan.FromSeconds 3.
            let mutable found = false
            while not found && DateTime.UtcNow < deadline do
                let! row =
                    fixture.QuerySingleOrDefault<{| kind: string |}>(
                        "SELECT kind FROM llm_usage WHERE kind = @kind AND chat_id = @chat_id ORDER BY id DESC LIMIT 1",
                        {| kind = kind; chat_id = chatId |})
                if box row <> null then
                    found <- true
                else
                    do! Task.Delay 200
            return found
        }

    let scripted (status: int) (body: string) : AzureScriptedResponse =
        { status = status; body = body; delayMs = 0; errorMode = "" }

    /// Non-streamed chat-completion body shape (/summary uses IChatCompletion.Complete,
    /// not the SSE stream LlmTests.fs's completionBody targets).
    let nonStreamCompletionBody (content: string) =
        let contentJson = JsonSerializer.Serialize(content)
        $"""{{"id":"chatcmpl-summary","object":"chat.completion","model":"gpt-5-mini-2025-08-07","choices":[{{"index":0,"finish_reason":"stop","message":{{"role":"assistant","content":{contentJson}}}}}],"usage":{{"prompt_tokens":300,"completion_tokens":80,"total_tokens":380}}}}"""

open CommandsTestHelpers

/// Phase-1 Slice 4: command dispatcher (registry, aliases, @suffix, unknown-command
/// fallthrough), /usage, /model, /summary.
type CommandsTests(fixture: AlitaTestContainers) =

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

    // ── Dispatcher parsing ──────────────────────────────────────────────────

    [<Fact>]
    let ``help lists every registered command`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 8001L, username = "help_alice", firstName = "Alice")
            let update = Tg.groupMessage("/help", user, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! replies = botReplyRows msgId
            Assert.Single(replies) |> ignore
            let text = replies[0].text
            for name in [ "/img"; "/model"; "/summary"; "/usage"; "/help" ] do
                Assert.Contains(name, text)
        }

    [<Fact>]
    let ``start is an alias for help`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 8002L, username = "help_bob", firstName = "Bob")
            let update = Tg.groupMessage("/start", user, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! replies = botReplyRows msgId
            Assert.Single(replies) |> ignore
            Assert.Contains("/summary", replies[0].text)
        }

    [<Fact>]
    let ``command addressed to a different bot via bot-username suffix is not dispatched`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 8003L, username = "help_carol", firstName = "Carol")
            let text = "/help@some_other_bot"
            let update = Tg.groupMessage(text, user, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            // Falls through to the normal message path: logged verbatim, no command reply
            // (no mention/name trigger in the text either, so no reply of any kind).
            let! row = senderRow fixture.TargetChatId msgId
            Assert.NotNull(box row)
            Assert.Equal(text, row.text)
            let! replies = botReplyRows msgId
            Assert.Empty replies
        }

    [<Fact>]
    let ``img with the correct bot-username suffix still dispatches`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()
            let user = Tg.user(id = 8004L, username = "help_dave", firstName = "Dave")
            let prompt = "рассвет над горами"
            let update = Tg.groupMessage($"/img@{fixture.BotUsername} {prompt}", user, fixture.TargetChatId)

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! photoSends = fixture.GetFakeCalls("sendPhoto")
            Assert.NotEmpty(photoSends |> Array.filter (isToChat fixture.TargetChatId))
        }

    [<Fact>]
    let ``unrecognized slash command is ignored (falls through to plain logging)`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 8005L, username = "help_eve", firstName = "Eve")
            let text = "/totallyMadeUpCommand with args"
            let update = Tg.groupMessage(text, user, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! row = senderRow fixture.TargetChatId msgId
            Assert.NotNull(box row)
            Assert.Equal(text, row.text) // not rewritten into "[xxx-cmd] ..." — never matched a command
            let! replies = botReplyRows msgId
            Assert.Empty replies
        }

    // ── /model ────────────────────────────────────────────────────────────

    [<Fact>]
    let ``model with no arg shows the real model name, not the deployment id`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 8101L, username = "model_alice", firstName = "Alice")
            let update = Tg.groupMessage("/model", user, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! replies = botReplyRows msgId
            Assert.Single(replies) |> ignore
            // Deployment ids ("alita-gpt-5-mini") are an infra detail — /model shows the
            // real model name ("gpt-5-mini") instead (staging feedback: "we don't have
            // our own models").
            Assert.Contains("gpt-5-mini", replies[0].text)
            Assert.DoesNotContain("alita-gpt-5-mini", replies[0].text)
        }

    [<Fact>]
    let ``model with no arg shows LLM_DEPLOYMENT verbatim when it has no LLM_MODELS entry`` () =
        task {
            try
                do! fixture.ClearFakeCalls()
                // Unknown/missing mapping = show the stored value verbatim, no cleverness —
                // a deployment id with no matching catalog entry (stale/incomplete LLM_MODELS)
                // must not be silently hidden or mangled.
                do! fixture.SetBotSetting("LLM_DEPLOYMENT", "some-unmapped-deployment")
                do! fixture.ReloadSettings()

                let user = Tg.user(id = 8107L, username = "model_henry", firstName = "Henry")
                let update = Tg.groupMessage("/model", user, fixture.TargetChatId)
                let msgId = update.Message.Value.MessageId

                let! resp = fixture.SendUpdate(update)
                resp.EnsureSuccessStatusCode() |> ignore

                let! replies = botReplyRows msgId
                Assert.Single(replies) |> ignore
                Assert.Contains("some-unmapped-deployment", replies[0].text)
            finally
                fixture.SetBotSetting("LLM_DEPLOYMENT", "alita-gpt-5-mini") |> ignore
                fixture.ReloadSettings() |> ignore
        }

    [<Fact>]
    let ``model with a non-allowlisted arg refuses and does not change LLM_DEPLOYMENT`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 8102L, username = "model_bob", firstName = "Bob")
            let update = Tg.groupMessage("/model gpt-nonexistent-9000", user, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! replies = botReplyRows msgId
            Assert.Single(replies) |> ignore
            Assert.DoesNotContain("переключена", replies[0].text)
            // The refusal's allowlist is also shown as display names, not deployment ids.
            Assert.Contains("gpt-5-mini", replies[0].text)
            Assert.DoesNotContain("alita-gpt-5-mini", replies[0].text)

            let! setting =
                fixture.QuerySingleOrDefault<{| value: string |}>(
                    "SELECT value FROM bot_setting WHERE key = 'LLM_DEPLOYMENT'",
                    {||})
            Assert.Equal("alita-gpt-5-mini", setting.value)
        }

    [<Fact>]
    let ``model with a raw deployment id arg is refused — only catalog model names are accepted`` () =
        task {
            do! fixture.ClearFakeCalls()
            // Zero string transformation: the deployment id is wire-call-only plumbing and
            // is never an accepted /model arg, even though it's the LLM_MODELS entry's own
            // "deployment" value — only the catalog's "model" name switches.
            let user = Tg.user(id = 8106L, username = "model_grace", firstName = "Grace")
            let update = Tg.groupMessage("/model alita-gpt-5-mini-2", user, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! replies = botReplyRows msgId
            Assert.Single(replies) |> ignore
            Assert.DoesNotContain("переключена", replies[0].text)

            let! setting =
                fixture.QuerySingleOrDefault<{| value: string |}>(
                    "SELECT value FROM bot_setting WHERE key = 'LLM_DEPLOYMENT'",
                    {||})
            Assert.Equal("alita-gpt-5-mini", setting.value)
        }

    [<Fact>]
    let ``model with a catalog model-name arg switches LLM_DEPLOYMENT immediately, in-process`` () =
        task {
            try
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()

                let switcher = Tg.user(id = 8103L, username = "model_carol", firstName = "Carol")
                let switchUpdate = Tg.groupMessage("/model gpt-5-mini-2", switcher, fixture.TargetChatId)
                let switchMsgId = switchUpdate.Message.Value.MessageId

                let! switchResp = fixture.SendUpdate(switchUpdate)
                switchResp.EnsureSuccessStatusCode() |> ignore

                let! switchReplies = botReplyRows switchMsgId
                Assert.Single(switchReplies) |> ignore
                Assert.Contains("gpt-5-mini-2", switchReplies[0].text)
                Assert.DoesNotContain("alita-gpt-5-mini-2", switchReplies[0].text)

                let! setting =
                    fixture.QuerySingleOrDefault<{| value: string |}>(
                        "SELECT value FROM bot_setting WHERE key = 'LLM_DEPLOYMENT'",
                        {||})
                // Persisted as the real deployment id (LLM_MODELS' "deployment" field for
                // the matched entry), not the model name typed in.
                Assert.Equal("alita-gpt-5-mini-2", setting.value)
            finally
                fixture.SetBotSetting("LLM_DEPLOYMENT", "alita-gpt-5-mini") |> ignore
                fixture.ReloadSettings() |> ignore
        }

    [<Fact>]
    let ``model switch takes effect immediately, in-process`` () =
        task {
            try
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()

                let switcher = Tg.user(id = 8103L, username = "model_carol", firstName = "Carol")
                let switchUpdate = Tg.groupMessage("/model gpt-5-mini-2", switcher, fixture.TargetChatId)
                let switchMsgId = switchUpdate.Message.Value.MessageId

                let! switchResp = fixture.SendUpdate(switchUpdate)
                switchResp.EnsureSuccessStatusCode() |> ignore

                let! switchReplies = botReplyRows switchMsgId
                Assert.Single(switchReplies) |> ignore

                // No explicit fixture.ReloadSettings() call here on purpose — the switch
                // must already be live in-process (BotInfra.ISettingsReloader), same path
                // /reload-settings itself uses, per the plan's "refresh the LiveOptions
                // in-process" requirement.
                do! fixture.SetBotSetting("RESPONDER_MODE", "llm")
                do! fixture.ReloadSettings() // only to flip RESPONDER_MODE for this probe
                do! fixture.SetAzureLlmScript [| scripted 200 (nonStreamCompletionBody "ok") |]

                let mentioner = Tg.user(id = 8104L, username = "model_dave", firstName = "Dave")
                let mention = $"@{fixture.BotUsername}"
                let entities = [| MessageEntity.Create(``type`` = "mention", offset = 0L, length = int64 mention.Length) |]
                let triggerUpdate =
                    Tg.quickMsg(
                        text = $"{mention} привет",
                        chat = Tg.chat(id = fixture.TargetChatId),
                        from = mentioner,
                        entities = entities)

                let! triggerResp = fixture.SendUpdate(triggerUpdate)
                triggerResp.EnsureSuccessStatusCode() |> ignore

                let! llmCalls = fixture.GetAzureLlmCalls()
                Assert.True(
                    llmCalls |> Array.exists (fun c -> c.Url.Contains "alita-gpt-5-mini-2"),
                    $"""expected an Azure LLM call against the switched deployment; saw: {llmCalls |> Array.map (fun c -> c.Url) |> String.concat ", "}""")
            finally
                fixture.SetBotSetting("LLM_DEPLOYMENT", "alita-gpt-5-mini") |> ignore
                fixture.SetBotSetting("RESPONDER_MODE", "echo") |> ignore
                fixture.ReloadSettings() |> ignore
                fixture.SetAzureLlmScript [||] |> ignore
        }

    // ── /summary ──────────────────────────────────────────────────────────

    [<Fact>]
    let ``summary transcribes recent messages and replies with the scripted digest`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()
            do! fixture.SetAzureLlmScript [| scripted 200 (nonStreamCompletionBody "Итог: все спорили про котиков.") |]

            let chatter = Tg.user(id = 8201L, username = "summary_alice", firstName = "Alice")
            let! chatResp = fixture.SendUpdate(Tg.groupMessage("котики лучше собак, это факт", chatter, fixture.TargetChatId))
            chatResp.EnsureSuccessStatusCode() |> ignore

            let requester = Tg.user(id = 8202L, username = "summary_bob", firstName = "Bob")
            let summaryUpdate = Tg.groupMessage("/summary 50", requester, fixture.TargetChatId)
            let summaryMsgId = summaryUpdate.Message.Value.MessageId

            let! resp = fixture.SendUpdate(summaryUpdate)
            resp.EnsureSuccessStatusCode() |> ignore

            let! replies = botReplyRows summaryMsgId
            Assert.Single(replies) |> ignore
            Assert.Equal("Итог: все спорили про котиков.", replies[0].text)

            // The scripted transcript was actually built from message_log — sanity-check
            // the fake LLM call's request body carries the seeded chat content (parsed via
            // JsonDocument, not raw-substring — see requestMessagesContain's doc comment).
            let! llmCalls = fixture.GetAzureLlmCalls()
            Assert.True(
                llmCalls |> Array.exists (fun c -> requestMessagesContain c "котики лучше собак"),
                "expected the /summary transcript (seeded chat message) in the LLM request body")

            // /summary replies are now plain, whole-chat-visible sends (ephemeral
            // `receiver_user_id` delivery was retired after staging feedback found it
            // invisible in practice — see BotHelpers.fs and src/AlitaBot/README.md's
            // "Ephemeral message probe [RETIRED]"). Pin both halves of that: the wire
            // request carries no receiver_user_id, and the logged reply keeps its real,
            // nonzero sequential message_id (not the ephemeral MessageId=0 quirk).
            let! sends = fixture.GetFakeCalls("sendMessage")
            let summaryReplySend =
                sends
                |> Array.filter (isToChat fixture.TargetChatId)
                |> Array.filter (fun c -> jsonString c "text" = "Итог: все спорили про котиков.")
            Assert.NotEmpty summaryReplySend
            Assert.False(
                summaryReplySend |> Array.exists (fun c -> jsonHasNumberProperty c "receiver_user_id"),
                "expected the /summary reply's sendMessage call to be a normal reply, not receiver-scoped")
            Assert.NotEqual(0L, replies[0].message_id)
        }

    [<Fact>]
    let ``summary with default count arg parses and replies`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()
            do! fixture.SetAzureLlmScript [| scripted 200 (nonStreamCompletionBody "дефолтный итог") |]

            let user = Tg.user(id = 8203L, username = "summary_carol", firstName = "Carol")
            // No count arg -> SummaryDefaultCount (200), not the SummaryMaxCount cap.
            let update = Tg.groupMessage("/summary", user, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! replies = botReplyRows msgId
            Assert.Single(replies) |> ignore
            Assert.Equal("дефолтный итог", replies[0].text)
        }

    [<Fact>]
    let ``chat and stt calls each write an llm_usage row`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()
            do! fixture.SetAzureLlmScript [| scripted 200 (nonStreamCompletionBody "итог") |]

            let user = Tg.user(id = 8301L, username = "usagewrite_alice", firstName = "Alice")
            let! resp = fixture.SendUpdate(Tg.groupMessage("/summary 5", user, fixture.TargetChatId))
            resp.EnsureSuccessStatusCode() |> ignore

            let! chatRowSeen = awaitUsageRow fixture "chat" fixture.TargetChatId
            Assert.True(chatRowSeen, "expected an llm_usage row with kind='chat' after /summary")

            do! fixture.SetAzureSttScript [| scripted 200 """{"text":"голосовая расшифровка для учёта"}""" |]
            let voiceUser = Tg.user(id = 8302L, username = "usagewrite_bob", firstName = "Bob")
            let! voiceResp = fixture.SendUpdate(Tg.groupVoiceMessage(voiceUser, fixture.TargetChatId))
            voiceResp.EnsureSuccessStatusCode() |> ignore

            let! sttRowSeen = awaitUsageRow fixture "stt" fixture.TargetChatId
            Assert.True(sttRowSeen, "expected an llm_usage row with kind='stt' after a voice message")
        }

    // ── /usage ────────────────────────────────────────────────────────────

    [<Fact>]
    let ``usage renders seeded totals by model and by user`` () =
        task {
            do! fixture.ClearFakeCalls()
            let usageChatId = fixture.TargetChatId

            // A message_log row for this user so UsageByUser can resolve a display_name.
            let namedUser = Tg.user(id = 8401L, username = "usage_named", firstName = "ИменнаяЮзерша")
            let! seedResp = fixture.SendUpdate(Tg.groupMessage("привет", namedUser, usageChatId))
            seedResp.EnsureSuccessStatusCode() |> ignore

            let now = DateTime.UtcNow
            do! insertUsageRow fixture now "chat" "alita-gpt-5-mini" 0.5 usageChatId namedUser.Id
            do! insertUsageRow fixture (now.AddDays(-2.)) "image" "alita-image" 0.25 usageChatId namedUser.Id
            do! insertUsageRow fixture (now.AddDays(-10.)) "chat" "alita-gpt-5-mini" 99.0 usageChatId namedUser.Id // outside the 7-day window

            let requester = Tg.user(id = 8402L, username = "usage_bob", firstName = "Bob")
            let update = Tg.groupMessage("/usage", requester, usageChatId)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! replies = botReplyRows msgId
            Assert.Single(replies) |> ignore
            let text = replies[0].text

            Assert.Contains("alita-gpt-5-mini", text)
            Assert.Contains("alita-image", text)
            Assert.Contains("ИменнаяЮзерша", text)
            Assert.DoesNotContain("99.0000", text) // the 10-day-old row must not leak into the 7-day totals
        }

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            ValueTask(task {
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()
                do! fixture.SetAzureLlmScript [||]
                do! fixture.SetAzureSttScript [||]
                do! fixture.SetBotSetting("RESPONDER_MODE", "echo")
                do! fixture.SetBotSetting("LLM_DEPLOYMENT", "alita-gpt-5-mini")
                do! fixture.ReloadSettings()
            } :> Task)

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(task {
                do! fixture.SetAzureLlmScript [||]
                do! fixture.SetAzureSttScript [||]
                do! fixture.SetBotSetting("RESPONDER_MODE", "echo")
                do! fixture.SetBotSetting("LLM_DEPLOYMENT", "alita-gpt-5-mini")
                do! fixture.ReloadSettings()
            } :> Task)
