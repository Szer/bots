namespace AlitaBot.Tests

open System
open System.Text.Json
open System.Threading.Tasks
open BotTestInfra
open Funogram.Telegram.Types
open Xunit

open CommandsTestHelpers

/// Phase-1 Slice 5b: per-person dossiers + nightly fact extraction (`/test/run-job?name=
/// dossier_nightly_update`), dedup, recall injection (ResponderService), `/dossier`,
/// `/forget-me`.
///
/// The fake chat-completions endpoint (`SetAzureLlmScript`) serves the extraction and
/// summary-merge calls the nightly job makes, in order (a shared FIFO queue — see
/// `MemoryTests.fs`/`CommandsTests.fs`); the fake embeddings endpoint's deterministic
/// hash-of-text vectors (`FakeAzureOcrApi.Embedding.embed`) give real cosine-similarity
/// behavior for dedup and recall without a real embedding model — texts sharing
/// vocabulary land close together, texts with disjoint vocabulary land near-orthogonal.
type DossierTests(fixture: AlitaTestContainers) =

    let botReplyRows (replyToMessageId: int64) =
        fixture.Query<MessageLogRow>(
            """
SELECT chat_id, message_id, user_id, username, display_name, is_bot, reply_to_message_id, text
FROM message_log WHERE chat_id = @cid AND is_bot = TRUE AND reply_to_message_id = @mid
ORDER BY message_id
""",
            {| cid = fixture.TargetChatId; mid = replyToMessageId |})

    /// JSON-array-of-strings body for the fake extraction LLM call — matches
    /// EXTRACT_PROMPT's contract (DossierService.parseFactsJson).
    let factsJson (facts: string list) = JsonSerializer.Serialize(facts: string list)

    /// Triggers the nightly job immediately (TEST_MODE-only endpoint, bypasses the
    /// lease/schedule check entirely — see ScheduledJobs.fs's SchedulerHostedService).
    let runJob () =
        task {
            let! resp = fixture.Bot.PostAsync("/test/run-job?name=dossier_nightly_update", null)
            resp.EnsureSuccessStatusCode() |> ignore
        }

    let activeFacts (userId: int64) =
        fixture.Query<{| content: string |}>(
            "SELECT content FROM interaction_memory WHERE user_id = @uid AND valid_to IS NULL ORDER BY id",
            {| uid = userId |})

    // Column order matches the anonymous record's alphabetically-sorted underlying field
    // order (F# anonymous records sort fields alphabetically regardless of source order) —
    // Dapper's anonymous-type materialization requires the two to line up exactly.
    let dossierRow (userId: int64) =
        fixture.QuerySingleOrDefault<{| summary: string; user_id: int64 |}>(
            "SELECT summary, user_id FROM person_dossier WHERE user_id = @uid",
            {| uid = userId |})

    // ── Nightly extraction + dossier creation ───────────────────────────────

    [<Fact>]
    let ``run-job extracts a fact and creates interaction_memory + person_dossier rows`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()

            let user = Tg.user(id = 9301L, username = "dossier_alice", firstName = "Alice")
            let! seedResp = fixture.SendUpdate(Tg.groupMessage("я обожаю программировать на F#", user, fixture.TargetChatId))
            seedResp.EnsureSuccessStatusCode() |> ignore

            do! fixture.SetAzureLlmScript
                    [| scripted 200 (nonStreamCompletionBody (factsJson [ "Любит программировать на F#" ]))
                       scripted 200 (nonStreamCompletionBody "Алиса — энтузиастка F#, недавно упомянула, что любит на нём программировать.") |]

            do! runJob ()

            let! facts = activeFacts user.Id
            Assert.Single(facts) |> ignore
            Assert.Equal("Любит программировать на F#", facts[0].content)

            let! dossier = dossierRow user.Id
            Assert.NotNull(box dossier)
            Assert.Contains("F#", dossier.summary)

            let! llmCalls = fixture.GetAzureLlmCalls()
            Assert.True(
                llmCalls |> Array.exists (fun c -> requestMessagesContain c "F#"),
                "expected the seeded message to feed the extraction LLM call")
        }

    [<Fact>]
    let ``a user with no messages in the last 24h is skipped entirely`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()
            do! fixture.SetAzureLlmScript [||]

            do! runJob ()

            let! llmCalls = fixture.GetAzureLlmCalls()
            Assert.Empty llmCalls
        }

    // ── Dedup ────────────────────────────────────────────────────────────────

    [<Fact>]
    let ``the same fact scripted across two nightly runs yields only one active interaction_memory row`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()

            let user = Tg.user(id = 9302L, username = "dossier_bob", firstName = "Bob")
            let fact = "Ненавидит YAML"

            let! seed1 = fixture.SendUpdate(Tg.groupMessage("ненавижу YAML всей душой", user, fixture.TargetChatId))
            seed1.EnsureSuccessStatusCode() |> ignore

            do! fixture.SetAzureLlmScript
                    [| scripted 200 (nonStreamCompletionBody (factsJson [ fact ]))
                       scripted 200 (nonStreamCompletionBody "Боб на дух не переносит YAML.") |]
            do! runJob ()

            let! facts1 = activeFacts user.Id
            Assert.Single(facts1) |> ignore

            // Second run: user active again, extraction scripts the SAME fact text again —
            // NearestActiveFactSimilarity finds cosine 1.0 against the existing row (same
            // deterministic hash vector for identical text), so it must be skipped, not
            // inserted as a second active row.
            let! seed2 = fixture.SendUpdate(Tg.groupMessage("да, действительно ненавижу этот формат", user, fixture.TargetChatId))
            seed2.EnsureSuccessStatusCode() |> ignore

            do! fixture.SetAzureLlmScript [| scripted 200 (nonStreamCompletionBody (factsJson [ fact ])) |]
            do! runJob ()

            let! facts2 = activeFacts user.Id
            Assert.Single(facts2) |> ignore
            Assert.Equal(fact, facts2[0].content)
        }

    // ── Recall injection (ResponderService) ─────────────────────────────────

    [<Fact>]
    let ``a triggered message's author gets their dossier fact injected into the system prompt, other authors don't`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()

            let author = Tg.user(id = 9401L, username = "dossier_carol", firstName = "Carol")
            let! seedResp = fixture.SendUpdate(Tg.groupMessage("обожаю кофе по утрам", author, fixture.TargetChatId))
            seedResp.EnsureSuccessStatusCode() |> ignore

            let fact = "Обожает кофе по утрам"
            do! fixture.SetAzureLlmScript
                    [| scripted 200 (nonStreamCompletionBody (factsJson [ fact ]))
                       scripted 200 (nonStreamCompletionBody "Кэрол — большая любительница утреннего кофе.") |]
            do! runJob ()

            let! dossier = dossierRow author.Id
            Assert.NotNull(box dossier)

            try
                do! fixture.SetBotSetting("RESPONDER_MODE", "llm")
                do! fixture.ReloadSettings()

                // Repeats the fact's own vocabulary near-verbatim -> cosine similarity
                // comfortably above DOSSIER_SIM_FLOOR (0.60) even after the bot's own
                // @username tokens (part of the raw text, unrelated vocabulary) dilute the
                // fake's bag-of-words vector; the reply content itself is irrelevant here.
                do! fixture.ClearAzureOcrCalls()
                do! fixture.SetAzureLlmScript [| scripted 200 (nonStreamCompletionBody "ok") |]
                let mention = $"@{fixture.BotUsername}"
                let authorText = $"{mention} обожает кофе по утрам"
                let authorEntities = [| MessageEntity.Create(``type`` = "mention", offset = 0L, length = int64 mention.Length) |]
                let authorTrigger =
                    Tg.quickMsg(text = authorText, chat = Tg.chat(id = fixture.TargetChatId), from = author, entities = authorEntities)
                let! authorResp = fixture.SendUpdate(authorTrigger)
                authorResp.EnsureSuccessStatusCode() |> ignore

                let! llmCalls = fixture.GetAzureLlmCalls()
                Assert.True(
                    llmCalls |> Array.exists (fun c -> requestMessagesContain c fact),
                    "expected the triggering author's dossier fact in the LLM request")

                // A different author with no dossier of their own -> no fact leak.
                do! fixture.ClearAzureOcrCalls()
                do! fixture.SetAzureLlmScript [| scripted 200 (nonStreamCompletionBody "ok") |]
                let other = Tg.user(id = 9402L, username = "dossier_dave", firstName = "Dave")
                let otherText = $"{mention} расскажи что-нибудь про погоду"
                let otherEntities = [| MessageEntity.Create(``type`` = "mention", offset = 0L, length = int64 mention.Length) |]
                let otherTrigger =
                    Tg.quickMsg(text = otherText, chat = Tg.chat(id = fixture.TargetChatId), from = other, entities = otherEntities)
                let! otherResp = fixture.SendUpdate(otherTrigger)
                otherResp.EnsureSuccessStatusCode() |> ignore

                let! llmCalls2 = fixture.GetAzureLlmCalls()
                Assert.False(
                    llmCalls2 |> Array.exists (fun c -> requestMessagesContain c fact),
                    "expected no dossier fact leak for a different, dossier-less author")
            finally
                fixture.SetBotSetting("RESPONDER_MODE", "echo") |> ignore
                fixture.ReloadSettings() |> ignore
                fixture.SetAzureLlmScript [||] |> ignore
        }

    [<Fact>]
    let ``DOSSIER_ENABLED=false disables recall injection entirely`` () =
        task {
            try
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()

                let author = Tg.user(id = 9403L, username = "dossier_erin2", firstName = "Erin")
                let! seedResp = fixture.SendUpdate(Tg.groupMessage("обожаю бег по утрам", author, fixture.TargetChatId))
                seedResp.EnsureSuccessStatusCode() |> ignore

                let fact = "Обожает бег по утрам"
                do! fixture.SetAzureLlmScript
                        [| scripted 200 (nonStreamCompletionBody (factsJson [ fact ]))
                           scripted 200 (nonStreamCompletionBody "Эрин бегает по утрам.") |]
                do! runJob ()

                do! fixture.SetBotSetting("RESPONDER_MODE", "llm")
                do! fixture.SetBotSetting("DOSSIER_ENABLED", "false")
                do! fixture.ReloadSettings()

                do! fixture.ClearAzureOcrCalls()
                do! fixture.SetAzureLlmScript [| scripted 200 (nonStreamCompletionBody "ok") |]
                let mention = $"@{fixture.BotUsername}"
                let text = $"{mention} какой бег я люблю по утрам?"
                let entities = [| MessageEntity.Create(``type`` = "mention", offset = 0L, length = int64 mention.Length) |]
                let trigger = Tg.quickMsg(text = text, chat = Tg.chat(id = fixture.TargetChatId), from = author, entities = entities)
                let! resp = fixture.SendUpdate(trigger)
                resp.EnsureSuccessStatusCode() |> ignore

                let! llmCalls = fixture.GetAzureLlmCalls()
                Assert.False(
                    llmCalls |> Array.exists (fun c -> requestMessagesContain c fact),
                    "expected no dossier fact injection when DOSSIER_ENABLED=false")
            finally
                fixture.SetBotSetting("RESPONDER_MODE", "echo") |> ignore
                fixture.SetBotSetting("DOSSIER_ENABLED", "true") |> ignore
                fixture.ReloadSettings() |> ignore
                fixture.SetAzureLlmScript [||] |> ignore
        }

    // ── /dossier ─────────────────────────────────────────────────────────────

    [<Fact>]
    let ``dossier with no arg and no history replies with the empty placeholder`` () =
        task {
            do! fixture.ClearFakeCalls()

            let user = Tg.user(id = 9601L, username = "dossier_frank", firstName = "Frank")
            let update = Tg.groupMessage("/dossier", user, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! replies = botReplyRows msgId
            Assert.Single(replies) |> ignore
            Assert.Contains("пусто", replies[0].text)
        }

    [<Fact>]
    let ``dossier with a username argument renders that person's summary and facts`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()

            let user = Tg.user(id = 9602L, username = "dossier_grace", firstName = "Grace")
            let! seedResp = fixture.SendUpdate(Tg.groupMessage("обожаю велоспорт по выходным", user, fixture.TargetChatId))
            seedResp.EnsureSuccessStatusCode() |> ignore

            do! fixture.SetAzureLlmScript
                    [| scripted 200 (nonStreamCompletionBody (factsJson [ "Любит велоспорт по выходным" ]))
                       scripted 200 (nonStreamCompletionBody "Грейс увлекается велоспортом по выходным.") |]
            do! runJob ()

            let requester = Tg.user(id = 9603L, username = "dossier_henry", firstName = "Henry")
            let update = Tg.groupMessage("/dossier @dossier_grace", requester, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! replies = botReplyRows msgId
            Assert.Single(replies) |> ignore
            Assert.Contains("велоспорт", replies[0].text)
            Assert.Contains("Любит велоспорт по выходным", replies[0].text)
        }

    [<Fact>]
    let ``dossier for an unknown username replies with the empty placeholder`` () =
        task {
            do! fixture.ClearFakeCalls()

            let requester = Tg.user(id = 9604L, username = "dossier_ivan", firstName = "Ivan")
            let update = Tg.groupMessage("/dossier @totally_unknown_username_xyz", requester, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! replies = botReplyRows msgId
            Assert.Single(replies) |> ignore
            Assert.Contains("пусто", replies[0].text)
        }

    // ── /forget-me ───────────────────────────────────────────────────────────

    [<Fact>]
    let ``forget-me purges dossier data, and subsequent messages are skipped by the embedding pipeline and nightly job`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()

            let user = Tg.user(id = 9701L, username = "dossier_judy", firstName = "Judy")

            // Build up dossier data first, so PurgeUserMemory has something real to delete.
            let! seed1 = fixture.SendUpdate(Tg.groupMessage("обожаю акварельную живопись", user, fixture.TargetChatId))
            seed1.EnsureSuccessStatusCode() |> ignore
            do! fixture.SetAzureLlmScript
                    [| scripted 200 (nonStreamCompletionBody (factsJson [ "Любит акварельную живопись" ]))
                       scripted 200 (nonStreamCompletionBody "Джуди увлекается акварельной живописью.") |]
            do! runJob ()

            let! factsBefore = activeFacts user.Id
            Assert.NotEmpty factsBefore
            let! dossierBefore = dossierRow user.Id
            Assert.NotNull(box dossierBefore)

            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()

            let forgetUpdate = Tg.groupMessage("/forget-me", user, fixture.TargetChatId)
            let forgetMsgId = forgetUpdate.Message.Value.MessageId
            let! forgetResp = fixture.SendUpdate(forgetUpdate)
            forgetResp.EnsureSuccessStatusCode() |> ignore

            let! forgetReplies = botReplyRows forgetMsgId
            Assert.Single(forgetReplies) |> ignore

            let! factsAfter = activeFacts user.Id
            Assert.Empty factsAfter
            let! dossierAfter = dossierRow user.Id
            Assert.Null(box dossierAfter)

            // Subsequent messages from the now-opted-out user are never embedded.
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()
            let! seed2 = fixture.SendUpdate(Tg.groupMessage("обожаю опять же акварель, серьёзно, это не проверка дедупа", user, fixture.TargetChatId))
            seed2.EnsureSuccessStatusCode() |> ignore
            do! Task.Delay 1500
            let! embCalls = fixture.GetAzureEmbeddingsCalls()
            Assert.Empty embCalls

            // And the nightly job skips them entirely (no extraction call for this user).
            do! fixture.ClearFakeCalls()
            do! fixture.SetAzureLlmScript [||]
            do! runJob ()
            let! factsStill = activeFacts user.Id
            Assert.Empty factsStill
        }

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            ValueTask(task {
                // Nightly-job tests scan ALL of message_log for "active users" — must start
                // from a clean slate, not whatever earlier test classes (or earlier
                // DossierTests facts) left behind. See TruncateMemoryTables's doc comment.
                do! fixture.TruncateMemoryTables()
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()
                do! fixture.SetAzureLlmScript [||]
                do! fixture.SetAzureEmbeddingsScript [||]
                do! fixture.SetBotSetting("RESPONDER_MODE", "echo")
                do! fixture.SetBotSetting("DOSSIER_ENABLED", "true")
                do! fixture.ReloadSettings()
            } :> Task)

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(task {
                do! fixture.SetAzureLlmScript [||]
                do! fixture.SetAzureEmbeddingsScript [||]
                do! fixture.SetBotSetting("RESPONDER_MODE", "echo")
                do! fixture.SetBotSetting("DOSSIER_ENABLED", "true")
                do! fixture.ReloadSettings()
            } :> Task)
