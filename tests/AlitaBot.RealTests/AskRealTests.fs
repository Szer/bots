namespace AlitaBot.RealTests

open System
open System.Threading.Tasks
open Dapper
open Npgsql
open Xunit

/// Phase-1 Slice 5a real-Telegram test: /ask semantic search against real Azure AI
/// Foundry embeddings + chat completions. Sends two GUID-marked factual messages (one
/// answers the question we'll ask, one is an unrelated decoy), waits for both to land in
/// message_embedding, then /ask's the fact and checks the reply actually references it —
/// exercising the full embed-question -> pgvector nearest-neighbor -> grounded-LLM-answer
/// round trip against a real embedding model (not the fake suite's deterministic
/// hash-of-text vectors, see MemoryTests.fs / src/AlitaBot/README.md's "Memory" section).
type AskRealTests(fx: RealAssemblyFixture) =
    let env = fx.Env

    /// Polls for at least one message_embedding row whose message_log text contains
    /// `marker` (a GUID makes this unambiguous) — the embedding pipeline is
    /// fire-and-forget (BotService.tryEmbed), so it can land slightly after the reply.
    let awaitEmbeddingForMarker (marker: string) =
        task {
            let deadline = DateTime.UtcNow + Timeouts.dbSettle
            let mutable found = false

            while not found && DateTime.UtcNow < deadline do
                use conn = new NpgsqlConnection(fx.DbConnectionString)
                let! count =
                    conn.ExecuteScalarAsync<int64>(
                        """
SELECT COUNT(*) FROM message_embedding me
JOIN message_log ml ON ml.id = me.message_log_id
WHERE ml.chat_id = @chat_id AND ml.text LIKE '%' || @marker || '%';
""",
                        {| chat_id = env.TestChatId; marker = marker |})

                found <- count > 0L
                if not found then do! Task.Delay 500

            return found
        }

    [<Fact>]
    member _.``ask answers a fact from a GUID-marked chat message and ignores an unrelated decoy``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            if env.ResponderMode <> "llm" then
                Assert.Skip
                    "RESPONDER_MODE=llm required (real /ask calls Azure AI Foundry embeddings + chat) — run `RESPONDER_MODE=llm make real-test`"

            let factGuid = Guid.NewGuid().ToString "N"
            let fact = $"{factGuid}: столица Австралии — Канберра"
            let! _factMsgId = fx.UserClient.SendText(env.TestChatId, fact)

            let decoyGuid = Guid.NewGuid().ToString "N"
            let decoy = $"{decoyGuid}: любимый напиток тестового бота — облепиховый чай"
            let! _decoyMsgId = fx.UserClient.SendText(env.TestChatId, decoy)

            let! factEmbedded = awaitEmbeddingForMarker factGuid
            Assert.True(
                factEmbedded,
                $"expected the fact message ({factGuid}) to get a message_embedding row within {Timeouts.dbSettle.TotalSeconds}s")
            let! decoyEmbedded = awaitEmbeddingForMarker decoyGuid
            Assert.True(
                decoyEmbedded,
                $"expected the decoy message ({decoyGuid}) to get a message_embedding row too (it just shouldn't match the question)")

            let! askMsgId = fx.UserClient.SendText(env.TestChatId, "/ask какая столица Австралии?")
            let! reply = fx.UserClient.AwaitReplyTo(env.TestChatId, askMsgId, Timeouts.reply)

            Assert.False(String.IsNullOrWhiteSpace reply.message)
            Assert.True(
                reply.message.Contains("Канберра", StringComparison.OrdinalIgnoreCase)
                || reply.message.Contains factGuid,
                $"expected the /ask reply to reference the seeded fact (Канберра) or its marker ({factGuid}): {reply.message}")
        })
