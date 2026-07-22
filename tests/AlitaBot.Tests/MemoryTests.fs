namespace AlitaBot.Tests

open System
open System.Threading.Tasks
open BotTestInfra
open Xunit

open CommandsTestHelpers

/// Phase-1 Slice 5a: pgvector memory foundation (inline embedding pipeline,
/// message_log -> message_embedding) and /ask semantic search.
///
/// The fake embeddings endpoint (FakeAzureOcrApi.Handlers.handleEmbeddings) returns
/// deterministic hash-of-text vectors (FakeAzureOcrApi.Embedding.embed): tokenize,
/// hash each token into one of 1536 dimensions, weight-and-normalize. Texts that share
/// vocabulary land close together (nonzero cosine similarity on the shared dimensions);
/// texts with disjoint vocabulary land near-orthogonal (~0 similarity). That's what lets
/// "ask answers from semantically relevant history..." below assert real semantic
/// separation without a real embedding model.
type MemoryTests(fixture: AlitaTestContainers) =

    let botReplyRows (replyToMessageId: int64) =
        fixture.Query<MessageLogRow>(
            """
SELECT chat_id, message_id, user_id, username, display_name, is_bot, reply_to_message_id, text
FROM message_log WHERE chat_id = @cid AND is_bot = TRUE AND reply_to_message_id = @mid
ORDER BY message_id
""",
            {| cid = fixture.TargetChatId; mid = replyToMessageId |})

    let chatCompletionCalls () =
        task {
            let! calls = fixture.GetAzureLlmCalls()
            return calls |> Array.filter (fun c -> c.Url.Contains "/chat/completions")
        }

    /// Polls for a message_embedding row keyed by (chat_id, message_id) — the embedding
    /// pipeline (BotService.tryEmbed) is fire-and-forget, so it can land slightly after
    /// the reply does. Returns once found, or `false` once `timeoutMs` elapses.
    let awaitEmbeddingRow (chatId: int64) (messageId: int64) (timeoutMs: int) =
        task {
            let deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(float timeoutMs)
            let mutable found = false
            while not found && DateTime.UtcNow < deadline do
                let! row =
                    fixture.QuerySingleOrDefault<{| message_log_id: int64 |}>(
                        """
SELECT me.message_log_id
FROM message_embedding me
JOIN message_log ml ON ml.id = me.message_log_id
WHERE ml.chat_id = @chat_id AND ml.message_id = @message_id
""",
                        {| chat_id = chatId; message_id = messageId |})
                if box row <> null then
                    found <- true
                else
                    do! Task.Delay 200
            return found
        }

    // ── Embedding pipeline ───────────────────────────────────────────────────

    [<Fact>]
    let ``a logged message gets a message_embedding row within 5s`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()

            let user = Tg.user(id = 9001L, username = "mem_alice", firstName = "Alice")
            let update = Tg.groupMessage("Планируем встречу в офисе на завтра", user, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! found = awaitEmbeddingRow fixture.TargetChatId msgId 5000
            Assert.True(found, "expected a message_embedding row for the logged message within 5s")

            let! embCalls = fixture.GetAzureEmbeddingsCalls()
            Assert.NotEmpty embCalls
        }

    [<Fact>]
    let ``a short (below EMBEDDING_MIN_CHARS) message is never embedded`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()

            // EMBEDDING_MIN_CHARS defaults to 3 (see ContainerTestBase's seed) — "ок" is
            // 2 chars, below the floor, and never matches a trigger so it's just logged.
            let user = Tg.user(id = 9002L, username = "mem_short", firstName = "Short")
            let update = Tg.groupMessage("ок", user, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! found = awaitEmbeddingRow fixture.TargetChatId msgId 2000
            Assert.False(found, "expected no message_embedding row for a message shorter than EMBEDDING_MIN_CHARS")
        }

    [<Fact>]
    let ``EMBED_MESSAGES=false disables the embedding pipeline entirely`` () =
        task {
            try
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()
                do! fixture.SetBotSetting("EMBED_MESSAGES", "false")
                do! fixture.ReloadSettings()

                let user = Tg.user(id = 9003L, username = "mem_bob", firstName = "Bob")
                let update = Tg.groupMessage("Это сообщение не должно попасть в эмбеддинги", user, fixture.TargetChatId)
                let msgId = update.Message.Value.MessageId

                let! resp = fixture.SendUpdate(update)
                resp.EnsureSuccessStatusCode() |> ignore

                // Same-order-of-magnitude window as the positive test's poll, so a flag
                // that was accidentally ignored (embedding just hasn't landed yet) would
                // still show up as a false negative here rather than a silent pass.
                let! found = awaitEmbeddingRow fixture.TargetChatId msgId 3000
                Assert.False(found, "expected no message_embedding row when EMBED_MESSAGES=false")

                let! embCalls = fixture.GetAzureEmbeddingsCalls()
                Assert.Empty embCalls
            finally
                fixture.SetBotSetting("EMBED_MESSAGES", "true") |> ignore
                fixture.ReloadSettings() |> ignore
        }

    // ── /ask ──────────────────────────────────────────────────────────────

    [<Fact>]
    let ``ask answers from semantically relevant history and excludes irrelevant messages`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()
            do! fixture.SetAzureLlmScript [| scripted 200 (nonStreamCompletionBody "Столица Австралии — Канберра.") |]

            let chatter = Tg.user(id = 9101L, username = "mem_carol", firstName = "Carol")

            // Shares "столица"/"австралии" with the question below -> cosine similarity
            // well above the 0.5 floor (ASK_SIM_FLOOR).
            let relevantUpdate = Tg.groupMessage("Столица Австралии — Канберра", chatter, fixture.TargetChatId)
            let relevantMsgId = relevantUpdate.Message.Value.MessageId
            let! relevantResp = fixture.SendUpdate(relevantUpdate)
            relevantResp.EnsureSuccessStatusCode() |> ignore

            // Disjoint vocabulary from the question -> ~0 similarity, must not appear in
            // the /ask context even though it's embedded (and therefore a valid nearest-K
            // candidate before the similarity floor is applied).
            let irrelevantUpdate = Tg.groupMessage("Рецепт борща с капустой", chatter, fixture.TargetChatId)
            let irrelevantMsgId = irrelevantUpdate.Message.Value.MessageId
            let! irrelevantResp = fixture.SendUpdate(irrelevantUpdate)
            irrelevantResp.EnsureSuccessStatusCode() |> ignore

            let! relevantEmbedded = awaitEmbeddingRow fixture.TargetChatId relevantMsgId 5000
            Assert.True(relevantEmbedded, "expected the relevant seeded message to be embedded")
            let! irrelevantEmbedded = awaitEmbeddingRow fixture.TargetChatId irrelevantMsgId 5000
            Assert.True(irrelevantEmbedded, "expected the irrelevant seeded message to be embedded too (it just shouldn't match)")

            let asker = Tg.user(id = 9102L, username = "mem_dave", firstName = "Dave")
            let askUpdate = Tg.groupMessage("/ask столица австралии", asker, fixture.TargetChatId)
            let askMsgId = askUpdate.Message.Value.MessageId

            let! askResp = fixture.SendUpdate(askUpdate)
            askResp.EnsureSuccessStatusCode() |> ignore

            let! replies = botReplyRows askMsgId
            Assert.Single(replies) |> ignore
            Assert.Equal("Столица Австралии — Канберра.", replies[0].text)

            let! llmCalls = chatCompletionCalls ()
            Assert.True(
                llmCalls |> Array.exists (fun c -> requestMessagesContain c "Канберра"),
                "expected the /ask context to include the semantically relevant seeded message")
            Assert.False(
                llmCalls |> Array.exists (fun c -> requestMessagesContain c "борща"),
                "expected the /ask context to exclude the semantically irrelevant seeded message")
        }

    [<Fact>]
    let ``ask with no matching history replies with a graceful no-match message and never calls the LLM`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()
            do! fixture.SetAzureLlmScript [||]

            let asker = Tg.user(id = 9201L, username = "mem_eve", firstName = "Eve")
            let update = Tg.groupMessage("/ask курс японской иены к сому в 1998 году", asker, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! replies = botReplyRows msgId
            Assert.Single(replies) |> ignore
            Assert.Contains("не нашла", replies[0].text)

            // Short-circuit path (DbService.SemanticSearch returned no rows above the
            // similarity floor) — no chat-completions call should have been made at all.
            let! llmCalls = chatCompletionCalls ()
            Assert.Empty llmCalls
        }

    [<Fact>]
    let ``ask with an empty question replies with a usage hint`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()

            let user = Tg.user(id = 9202L, username = "mem_frank", firstName = "Frank")
            let update = Tg.groupMessage("/ask", user, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! replies = botReplyRows msgId
            Assert.Single(replies) |> ignore
            Assert.Contains("/ask", replies[0].text)

            // No LLM call for the empty-question hint itself. (The hint reply's own text
            // does get embedded, same as any other bot reply — see the embedding pipeline
            // tests above — so this intentionally doesn't assert on embeddings calls.)
            let! llmCalls = chatCompletionCalls ()
            Assert.Empty llmCalls
        }

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            ValueTask(task {
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()
                do! fixture.SetAzureLlmScript [||]
                do! fixture.SetAzureEmbeddingsScript [||]
                do! fixture.SetBotSetting("EMBED_MESSAGES", "true")
                do! fixture.ReloadSettings()
            } :> Task)

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(task {
                do! fixture.SetAzureLlmScript [||]
                do! fixture.SetAzureEmbeddingsScript [||]
                do! fixture.SetBotSetting("EMBED_MESSAGES", "true")
                do! fixture.ReloadSettings()
            } :> Task)
