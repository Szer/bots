namespace AlitaBot.Tests

open System
open System.Text.Json
open System.Threading.Tasks
open BotTestInfra
open Dapper
open Npgsql
open Xunit

open CommandsTestHelpers

/// Slice 7: social engine — /roast, /awards, /quote, /karma.
///
/// Same idioms as DossierTests.fs: `SetAzureLlmScript` serves the fake chat-completions
/// endpoint in FIFO order, and the nightly dossier job (`/test/run-job`) is used to seed a
/// real `person_dossier`/`interaction_memory` fact before exercising `/roast` against it.
type SocialTests(fixture: AlitaTestContainers) =

    let botReplyRows (replyToMessageId: int64) =
        fixture.Query<MessageLogRow>(
            """
SELECT chat_id, message_id, user_id, username, display_name, is_bot, reply_to_message_id, text
FROM message_log WHERE chat_id = @cid AND is_bot = TRUE AND reply_to_message_id = @mid
ORDER BY message_id
""",
            {| cid = fixture.TargetChatId; mid = replyToMessageId |})

    /// JSON-array-of-strings body for the fake extraction LLM call — matches
    /// EXTRACT_PROMPT's contract, same helper as DossierTests.fs's factsJson.
    let factsJson (facts: string list) = JsonSerializer.Serialize(facts: string list)

    /// Strict JSON array of {title,user,evidence_quote} — the /awards LLM contract.
    let awardsJson (awards: (string * string * string) list) =
        awards
        |> List.map (fun (title, user, quote) -> {| title = title; user = user; evidence_quote = quote |})
        |> JsonSerializer.Serialize

    /// Strict JSON object {author,quote,comment} — the /quote LLM contract.
    let quoteJson (author: string) (quote: string) (comment: string) =
        JsonSerializer.Serialize {| author = author; quote = quote; comment = comment |}

    let runJob () =
        task {
            let! resp = fixture.Bot.PostAsync("/test/run-job?name=dossier_nightly_update", null)
            resp.EnsureSuccessStatusCode() |> ignore
        }

    /// Polls until `userId` has a `person_dossier` row — proof the nightly job actually
    /// finished (fire-and-forget on the bot side, see DossierTests.fs's header comment).
    let awaitDossierRow (userId: int64) (timeoutMs: int) =
        task {
            let deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(float timeoutMs)
            let mutable found = false
            while not found && DateTime.UtcNow < deadline do
                let! row =
                    fixture.QuerySingleOrDefault<{| summary: string |}>(
                        "SELECT summary FROM person_dossier WHERE user_id = @uid",
                        {| uid = userId |})
                if box row <> null then
                    found <- true
                else
                    do! Task.Delay 150
            return found
        }

    /// Directly inserts a `memory_opt_out` row, bypassing `/forget-me` — unlike
    /// `/forget-me`, this does NOT purge the person's existing dossier/facts, letting the
    /// opted-out test prove `/roast` skips reading them (rather than proving they simply
    /// don't exist).
    let optOutDirect (userId: int64) =
        task {
            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            do! conn.OpenAsync()
            let! _ =
                conn.ExecuteAsync(
                    "INSERT INTO memory_opt_out (user_id) VALUES (@uid) ON CONFLICT DO NOTHING;",
                    {| uid = userId |})
            return ()
        }

    // Column order matches the anonymous record's alphabetically-sorted underlying field
    // order (F# anonymous records sort fields alphabetically regardless of source order) —
    // Dapper's anonymous-type materialization requires the two to line up exactly (same
    // convention as DossierTests.fs's dossierRow).
    let karmaRowsByUsername (username: string) =
        fixture.Query<{| evidence: string; title: string; user_id: Nullable<int64>; username: string |}>(
            "SELECT evidence, title, user_id, username FROM karma WHERE username = @u ORDER BY id",
            {| u = username |})

    let karmaRowCount () =
        fixture.QuerySingleOrDefault<int64>("SELECT COUNT(*) FROM karma", {||})

    // ── /roast ───────────────────────────────────────────────────────────────

    [<Fact>]
    let ``roast uses a dossier fact and a verbatim recent message, then cooldown blocks a second call`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()
            do! fixture.TruncateSocialTables()
            // The nightly job scans ALL of message_log for "active users" — must start
            // from a clean slate (same reasoning as DossierTests.fs's own tests), otherwise
            // every user_id ever seeded by an earlier test class in this shared,
            // DisableTestParallelization=true assembly fixture would also get processed,
            // consuming this test's exact 2-response scripted queue out of order.
            do! fixture.TruncateMemoryTables()

            let target = Tg.user(id = 9801L, username = "roast_alice", firstName = "Alice")
            let seedText = "маркер_roast_alice обожает писать баги по пятницам"
            let! seedResp = fixture.SendUpdate(Tg.groupMessage(seedText, target, fixture.TargetChatId))
            seedResp.EnsureSuccessStatusCode() |> ignore

            let fact = "Обожает писать баги по пятницам"
            do! fixture.SetAzureLlmScript
                    [| scripted 200 (nonStreamCompletionBody (factsJson [ fact ]))
                       scripted 200 (nonStreamCompletionBody "Алиса плодит баги по пятницам.") |]
            do! runJob ()

            let! dossierFound = awaitDossierRow target.Id 5000
            Assert.True(dossierFound, "expected a person_dossier row for the roast target before roasting")

            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()
            do! fixture.SetAzureLlmScript [| scripted 200 (nonStreamCompletionBody "текст прожарки без спецсимволов") |]

            let roaster = Tg.user(id = 9802L, username = "roast_bob", firstName = "Bob")
            let update = Tg.groupMessage("/roast @roast_alice", roaster, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! replies = botReplyRows msgId
            Assert.Single(replies) |> ignore
            Assert.Equal("текст прожарки без спецсимволов", replies[0].text)

            let! llmCalls = fixture.GetAzureLlmCalls()
            Assert.Single(llmCalls) |> ignore
            Assert.True(requestMessagesContain llmCalls[0] fact, "expected the dossier fact in the roast LLM request")
            Assert.True(
                requestMessagesContain llmCalls[0] seedText,
                "expected the target's own recent message verbatim in the roast LLM request")

            // Cooldown: a second /roast against the same target immediately after -> a
            // fixed RU cooldown reply, no additional LLM call.
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()
            do! fixture.SetAzureLlmScript [||]

            let secondUpdate = Tg.groupMessage("/roast @roast_alice", roaster, fixture.TargetChatId)
            let secondMsgId = secondUpdate.Message.Value.MessageId
            let! secondResp = fixture.SendUpdate(secondUpdate)
            secondResp.EnsureSuccessStatusCode() |> ignore

            let! secondReplies = botReplyRows secondMsgId
            Assert.Single(secondReplies) |> ignore
            Assert.Contains("остыть", secondReplies[0].text)

            let! llmCallsAfterCooldown = fixture.GetAzureLlmCalls()
            Assert.Empty(llmCallsAfterCooldown)
        }

    [<Fact>]
    let ``roast on an opted-out target uses only their recent messages, never dossier or facts`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()
            do! fixture.TruncateSocialTables()
            do! fixture.TruncateMemoryTables()

            let target = Tg.user(id = 9803L, username = "roast_carol", firstName = "Carol")
            let seedText = "маркер_roast_carol обожает переписывать баш-скрипты на F#"
            let! seedResp = fixture.SendUpdate(Tg.groupMessage(seedText, target, fixture.TargetChatId))
            seedResp.EnsureSuccessStatusCode() |> ignore

            let fact = "Обожает переписывать баш-скрипты на F#"
            let summary = "Кэрол увлекается переписыванием скриптов."
            do! fixture.SetAzureLlmScript
                    [| scripted 200 (nonStreamCompletionBody (factsJson [ fact ]))
                       scripted 200 (nonStreamCompletionBody summary) |]
            do! runJob ()

            let! dossierFound = awaitDossierRow target.Id 5000
            Assert.True(dossierFound, "expected a person_dossier row before opting the target out")

            do! optOutDirect target.Id

            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()
            do! fixture.SetAzureLlmScript [| scripted 200 (nonStreamCompletionBody "прожарка только по её сообщениям") |]

            let roaster = Tg.user(id = 9804L, username = "roast_dave", firstName = "Dave")
            let update = Tg.groupMessage("/roast @roast_carol", roaster, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! replies = botReplyRows msgId
            Assert.Single(replies) |> ignore

            let! llmCalls = fixture.GetAzureLlmCalls()
            Assert.Single(llmCalls) |> ignore
            Assert.True(
                requestMessagesContain llmCalls[0] seedText,
                "expected the target's own recent message even though they're opted out")
            Assert.False(
                requestMessagesContain llmCalls[0] fact,
                "expected NO dossier fact in the prompt for an opted-out target")
            Assert.False(
                requestMessagesContain llmCalls[0] summary,
                "expected NO dossier summary in the prompt for an opted-out target")
        }

    // ── /awards ──────────────────────────────────────────────────────────────

    [<Fact>]
    let ``awards from scripted JSON renders titles and writes karma rows`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()
            do! fixture.TruncateSocialTables()

            let alice = Tg.user(id = 9901L, username = "awards_alice", firstName = "Alice")
            let! seed1 = fixture.SendUpdate(Tg.groupMessage("торможу тесты весь день", alice, fixture.TargetChatId))
            seed1.EnsureSuccessStatusCode() |> ignore

            do! fixture.SetAzureLlmScript
                    [| scripted 200 (nonStreamCompletionBody (awardsJson [ "Душнила недели", "@awards_alice", "торможу тесты весь день" ])) |]

            let requester = Tg.user(id = 9902L, username = "awards_bob", firstName = "Bob")
            let update = Tg.groupMessage("/awards", requester, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! replies = botReplyRows msgId
            Assert.Single(replies) |> ignore
            Assert.Contains("Душнила недели", replies[0].text)
            Assert.Contains("@awards_alice", replies[0].text)

            let! rows = karmaRowsByUsername "@awards_alice"
            Assert.Single(rows) |> ignore
            Assert.Equal("Душнила недели", rows[0].title)
            Assert.True(rows[0].user_id.HasValue, "expected the award's user_id to resolve via message_log.username")
            Assert.Equal(alice.Id, rows[0].user_id.Value)
        }

    /// Regression test for a real-LLM behavior seen in SocialRealTests.fs: even though
    /// AWARDS_PROMPT explicitly asks for the handle WITHOUT its surrounding `[...]`, a
    /// real model sometimes echoes the bracketed transcript form verbatim (e.g.
    /// `"[Ayrat Ru]"` instead of `"Ayrat Ru"`) — `stripUserHandleBrackets` must strip it
    /// so both `@username` resolution and the rendered/stored text stay clean.
    [<Fact>]
    let ``awards strips stray brackets from a scripted bracketed handle in the user field`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()
            do! fixture.TruncateSocialTables()

            let erin = Tg.user(id = 9910L, username = "awards_erin", firstName = "Erin")
            let! seed = fixture.SendUpdate(Tg.groupMessage("чиню чужой пайплайн в третий раз за день", erin, fixture.TargetChatId))
            seed.EnsureSuccessStatusCode() |> ignore

            do! fixture.SetAzureLlmScript
                    [| scripted
                           200
                           (nonStreamCompletionBody (
                               awardsJson [ "Вечный саппорт", "[@awards_erin]", "чиню чужой пайплайн в третий раз за день" ])) |]

            let requester = Tg.user(id = 9911L, username = "awards_frank", firstName = "Frank")
            let update = Tg.groupMessage("/awards", requester, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! replies = botReplyRows msgId
            Assert.Single(replies) |> ignore
            Assert.Contains("@awards_erin", replies[0].text)
            Assert.DoesNotContain("[@awards_erin]", replies[0].text)

            let! rows = karmaRowsByUsername "@awards_erin"
            Assert.Single(rows) |> ignore
            Assert.True(rows[0].user_id.HasValue, "expected @username resolution to still work once the brackets are stripped")
            Assert.Equal(erin.Id, rows[0].user_id.Value)
        }

    [<Fact>]
    let ``awards with malformed JSON twice fails gracefully, no crash, no karma rows`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()
            do! fixture.TruncateSocialTables()

            let user = Tg.user(id = 9903L, username = "awards_carol", firstName = "Carol")
            let! seed = fixture.SendUpdate(Tg.groupMessage("что-то было сказано", user, fixture.TargetChatId))
            seed.EnsureSuccessStatusCode() |> ignore

            do! fixture.SetAzureLlmScript
                    [| scripted 200 (nonStreamCompletionBody "это не json вообще")
                       scripted 200 (nonStreamCompletionBody "и это тоже не json") |]

            let requester = Tg.user(id = 9904L, username = "awards_dave", firstName = "Dave")
            let update = Tg.groupMessage("/awards", requester, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! replies = botReplyRows msgId
            Assert.Single(replies) |> ignore
            Assert.Contains("Не получилось", replies[0].text)

            let! llmCalls = fixture.GetAzureLlmCalls()
            Assert.Equal(2, llmCalls.Length)

            let! karmaCount = karmaRowCount ()
            Assert.Equal(0L, karmaCount)
        }

    // ── /quote ───────────────────────────────────────────────────────────────

    [<Fact>]
    let ``quote renders the scripted pick`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()

            let author = Tg.user(id = 9905L, username = "quote_alice", firstName = "Alice")
            let! seed = fixture.SendUpdate(Tg.groupMessage("а я говорю: сервер сам себя не задеплоит", author, fixture.TargetChatId))
            seed.EnsureSuccessStatusCode() |> ignore

            do! fixture.SetAzureLlmScript
                    [| scripted 200 (nonStreamCompletionBody (quoteJson "Alice" "сервер сам себя не задеплоит" "воистину")) |]

            let requester = Tg.user(id = 9906L, username = "quote_bob", firstName = "Bob")
            let update = Tg.groupMessage("/quote", requester, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! replies = botReplyRows msgId
            Assert.Single(replies) |> ignore
            Assert.Contains("сервер сам себя не задеплоит", replies[0].text)
            Assert.Contains("Alice", replies[0].text)
            Assert.Contains("воистину", replies[0].text)
        }

    // ── /karma ───────────────────────────────────────────────────────────────

    [<Fact>]
    let ``karma renders totals and newest titles`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateSocialTables()

            let target = Tg.user(id = 9907L, username = "karma_alice", firstName = "Alice")
            // Seed message_log so /karma @karma_alice can resolve the username to a user_id.
            let! seed = fixture.SendUpdate(Tg.groupMessage("что-то сказала", target, fixture.TargetChatId))
            seed.EnsureSuccessStatusCode() |> ignore

            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            do! conn.OpenAsync()
            for title in [ "Душнила недели"; "Пророк"; "Легенда чата" ] do
                let! _ =
                    conn.ExecuteAsync(
                        "INSERT INTO karma (user_id, username, title, evidence) VALUES (@uid, @u, @t, 'evidence');",
                        {| uid = target.Id; u = "@karma_alice"; t = title |})
                ()

            let requester = Tg.user(id = 9908L, username = "karma_bob", firstName = "Bob")
            let update = Tg.groupMessage("/karma @karma_alice", requester, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! replies = botReplyRows msgId
            Assert.Single(replies) |> ignore
            Assert.Contains("3", replies[0].text)
            for title in [ "Душнила недели"; "Пророк"; "Легенда чата" ] do
                Assert.Contains(title, replies[0].text)
        }

    [<Fact>]
    let ``karma with no awards replies gracefully`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateSocialTables()

            let user = Tg.user(id = 9909L, username = "karma_carol", firstName = "Carol")
            let update = Tg.groupMessage("/karma", user, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! replies = botReplyRows msgId
            Assert.Single(replies) |> ignore
            Assert.Contains("без наград", replies[0].text)
        }

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            ValueTask(task {
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()
                do! fixture.SetAzureLlmScript [||]
            } :> Task)

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(task {
                do! fixture.SetAzureLlmScript [||]
            } :> Task)
