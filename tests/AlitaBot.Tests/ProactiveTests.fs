namespace AlitaBot.Tests

open System
open System.Text.Json
open System.Threading.Tasks
open BotTestInfra
open Dapper
open Funogram.Telegram.Types
open Npgsql
open Xunit

open CommandsTestHelpers

/// Phase-1 Slice 8: proactive behavior — morning digest (`digest_daily` scheduled job),
/// willingness-gated interjections (burst + cooldown gated), meme reactions (vision LLM
/// strict-JSON contract). Every feature defaults OFF/0.0 (see dev-bot-settings.sql /
/// ContainerTestBase.fs's seed) — every test here flips only the setting(s) it needs on
/// for its own duration via `fixture.SetBotSetting` + `ReloadSettings`, restoring
/// afterward in a `finally` (PersonaTests' convention).
///
/// Interjection tests each use a DEDICATED chat id (never `fixture.TargetChatId`): the
/// cooldown check (`DbService.HasBotMessageSince`) looks at ALL bot messages ever logged
/// for a chat, and this fixture's frozen `FakeTimeProvider` means every bot reply ever
/// sent to `fixture.TargetChatId` by any earlier test in this shared,
/// `DisableTestParallelization=true` assembly run stays "recent" forever — using a fresh
/// chat id per scenario sidesteps that contamination instead of fighting it.
type ProactiveTests(fixture: AlitaTestContainers) =

    let runJob (name: string) =
        task {
            let! resp = fixture.Bot.PostAsync($"/test/run-job?name={name}", null)
            resp.EnsureSuccessStatusCode() |> ignore
        }

    let hasNoReplyParameters (call: FakeCall) =
        use doc = JsonDocument.Parse(call.Body: string)
        not (fst (doc.RootElement.TryGetProperty "reply_parameters"))

    /// Polls (up to `timeoutMs`) for at least `minCount` sendMessage calls to `chatId`.
    let awaitSendsToChat (chatId: int64) (minCount: int) (timeoutMs: int) =
        task {
            let deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(float timeoutMs)
            let mutable sends = [||]
            let mutable settled = false
            while not settled && DateTime.UtcNow < deadline do
                let! s = fixture.GetFakeCalls("sendMessage")
                sends <- s |> Array.filter (isToChat chatId)
                if sends.Length >= minCount then settled <- true else do! Task.Delay 150
            return sends
        }

    let awaitReactions (minCount: int) (timeoutMs: int) =
        task {
            let deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(float timeoutMs)
            let mutable reactions = [||]
            let mutable settled = false
            while not settled && DateTime.UtcNow < deadline do
                let! r = fixture.GetFakeCalls("setMessageReaction")
                reactions <- r
                if reactions.Length >= minCount then settled <- true else do! Task.Delay 150
            return reactions
        }

    /// Directly inserts an `is_bot=TRUE` message_log row (bypassing the app) to seed the
    /// "the bot recently spoke in this chat" precondition for the interjection cooldown
    /// test — cheaper and more deterministic than triggering a real reply through the app
    /// just to get a bot row logged. `NOW()` (the DB's own wall clock) is safe here even
    /// though the app's TimeProvider is frozen (TEST_MODE): the cooldown check compares
    /// against `frozen_now - cooldownMinutes`, and real wall-clock NOW() only ever moves
    /// forward from the frozen initial value, so `sent_at >= since` always holds.
    let insertBotMessage (chatId: int64) =
        task {
            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            do! conn.OpenAsync()
            let! _ =
                conn.ExecuteAsync(
                    """
INSERT INTO message_log (chat_id, message_id, user_id, username, display_name, is_bot, text, sent_at)
VALUES (@chat_id, @message_id, 1337, 'proactive_seed_bot', 'proactive_seed_bot', TRUE, 'seeded cooldown message', NOW())
ON CONFLICT (chat_id, message_id) DO NOTHING;
""",
                    {| chat_id = chatId; message_id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() |})
            return ()
        }

    let restoreDefaults () =
        task {
            do! fixture.SetBotSetting("TARGET_CHAT_IDS", string fixture.TargetChatId)
            do! fixture.SetBotSetting("DIGEST_ENABLED", "false")
            do! fixture.SetBotSetting("DIGEST_MIN_MESSAGES", "30")
            do! fixture.SetBotSetting("INTERJECT_PROBABILITY", "0.0")
            do! fixture.SetBotSetting("BURST_MSGS", "8")
            do! fixture.SetBotSetting("BURST_SPEAKERS", "3")
            do! fixture.SetBotSetting("BURST_WINDOW_MINUTES", "5")
            do! fixture.SetBotSetting("INTERJECT_COOLDOWN_MINUTES", "30")
            do! fixture.SetBotSetting("MEME_REACT_PROBABILITY", "0.0")
            do! fixture.SetAzureLlmScript [||]
            do! fixture.ReloadSettings()
        }

    // ── Morning digest ───────────────────────────────────────────────────────

    [<Fact>]
    let ``digest_daily sends a digest to a chat above the message threshold, skips one below it`` () =
        task {
            let secondChatId = -5100L
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()
            try
                do! fixture.SetBotSetting("TARGET_CHAT_IDS", $"{fixture.TargetChatId},{secondChatId}")
                do! fixture.SetBotSetting("DIGEST_ENABLED", "true")
                do! fixture.SetBotSetting("DIGEST_MIN_MESSAGES", "3")
                do! fixture.ReloadSettings()

                let busyUser = Tg.user(id = 91001L, username = "digest_busy", firstName = "Busy")
                for i in 1 .. 3 do
                    let! r = fixture.SendUpdate(Tg.groupMessage($"сообщение номер {i} про важное", busyUser, fixture.TargetChatId))
                    r.EnsureSuccessStatusCode() |> ignore

                let quietUser = Tg.user(id = 91002L, username = "digest_quiet", firstName = "Quiet")
                let! qr = fixture.SendUpdate(Tg.groupMessage("одно тихое сообщение", quietUser, secondChatId))
                qr.EnsureSuccessStatusCode() |> ignore

                let marker = "дайджест уникальный маркер восемь"
                do! fixture.SetAzureLlmScript [| scripted 200 (nonStreamCompletionBody marker) |]

                do! runJob "digest_daily"

                let! sends = awaitSendsToChat fixture.TargetChatId 1 8000
                Assert.NotEmpty sends
                Assert.Contains(sends, fun c -> (jsonString c "text").Contains(marker))
                Assert.Contains(sends, hasNoReplyParameters)

                // The below-threshold chat never got a digest — give the job a moment to
                // have processed it too (sequential for-loop over TargetChatIds) before
                // proving absence.
                do! Task.Delay 500
                let! allSends = fixture.GetFakeCalls("sendMessage")
                Assert.Empty(allSends |> Array.filter (isToChat secondChatId))
            finally
                restoreDefaults () |> ignore
        }

    // ── Willingness-gated interjections ──────────────────────────────────────

    [<Fact>]
    let ``p=1.0 with burst satisfied sends a scripted interjection as a plain (non-reply) message`` () =
        task {
            let chatId = -5201L
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()
            try
                do! fixture.SetBotSetting("TARGET_CHAT_IDS", $"{fixture.TargetChatId},{chatId}")
                do! fixture.SetBotSetting("INTERJECT_PROBABILITY", "1.0")
                do! fixture.SetBotSetting("BURST_MSGS", "3")
                do! fixture.SetBotSetting("BURST_SPEAKERS", "2")
                do! fixture.ReloadSettings()

                let marker = "интерджект уникальный маркер пять"
                do! fixture.SetAzureLlmScript [| scripted 200 (nonStreamCompletionBody marker) |]

                let userA = Tg.user(id = 92011L, username = "burst_a", firstName = "A")
                let userB = Tg.user(id = 92012L, username = "burst_b", firstName = "B")
                for u in [ userA; userB; userA ] do
                    let! r = fixture.SendUpdate(Tg.groupMessage("обычное сообщение в потоке", u, chatId))
                    r.EnsureSuccessStatusCode() |> ignore

                let! sends = awaitSendsToChat chatId 1 8000
                Assert.NotEmpty sends
                Assert.Contains(sends, fun c -> (jsonString c "text").Contains(marker))
                Assert.Contains(sends, hasNoReplyParameters)
            finally
                restoreDefaults () |> ignore
        }

    [<Fact>]
    let ``a scripted PASS response stays silent`` () =
        task {
            let chatId = -5202L
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()
            try
                do! fixture.SetBotSetting("TARGET_CHAT_IDS", $"{fixture.TargetChatId},{chatId}")
                do! fixture.SetBotSetting("INTERJECT_PROBABILITY", "1.0")
                do! fixture.SetBotSetting("BURST_MSGS", "3")
                do! fixture.SetBotSetting("BURST_SPEAKERS", "2")
                do! fixture.ReloadSettings()

                do! fixture.SetAzureLlmScript [| scripted 200 (nonStreamCompletionBody "  pass  ") |]

                let userA = Tg.user(id = 92021L, username = "pass_a", firstName = "A")
                let userB = Tg.user(id = 92022L, username = "pass_b", firstName = "B")
                for u in [ userA; userB; userA ] do
                    let! r = fixture.SendUpdate(Tg.groupMessage("обычное сообщение в потоке", u, chatId))
                    r.EnsureSuccessStatusCode() |> ignore

                // Prove absence, not just "not yet": wait out a generous window, then
                // confirm the LLM call itself did land (proves the PASS branch was
                // actually exercised, not that the interjection never fired at all).
                do! Task.Delay 2500
                let! llmCalls = fixture.GetAzureLlmCalls()
                Assert.NotEmpty llmCalls

                let! sends = fixture.GetFakeCalls("sendMessage")
                Assert.Empty(sends |> Array.filter (isToChat chatId))
            finally
                restoreDefaults () |> ignore
        }

    [<Fact>]
    let ``an active cooldown (a bot message just logged) skips the LLM call entirely`` () =
        task {
            let chatId = -5203L
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()
            try
                do! insertBotMessage chatId
                do! fixture.SetBotSetting("TARGET_CHAT_IDS", $"{fixture.TargetChatId},{chatId}")
                do! fixture.SetBotSetting("INTERJECT_PROBABILITY", "1.0")
                do! fixture.SetBotSetting("BURST_MSGS", "3")
                do! fixture.SetBotSetting("BURST_SPEAKERS", "2")
                do! fixture.SetBotSetting("INTERJECT_COOLDOWN_MINUTES", "30")
                do! fixture.ReloadSettings()
                do! fixture.SetAzureLlmScript [||]

                let userA = Tg.user(id = 92031L, username = "cooldown_a", firstName = "A")
                let userB = Tg.user(id = 92032L, username = "cooldown_b", firstName = "B")
                for u in [ userA; userB; userA ] do
                    let! r = fixture.SendUpdate(Tg.groupMessage("обычное сообщение в потоке", u, chatId))
                    r.EnsureSuccessStatusCode() |> ignore

                do! Task.Delay 2000
                let! llmCalls = fixture.GetAzureLlmCalls()
                Assert.Empty llmCalls
                let! sends = fixture.GetFakeCalls("sendMessage")
                Assert.Empty(sends |> Array.filter (isToChat chatId))
            finally
                restoreDefaults () |> ignore
        }

    [<Fact>]
    let ``p=0.0 never rolls an interjection — no LLM call even with burst satisfied`` () =
        task {
            let chatId = -5204L
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()
            try
                do! fixture.SetBotSetting("TARGET_CHAT_IDS", $"{fixture.TargetChatId},{chatId}")
                do! fixture.SetBotSetting("INTERJECT_PROBABILITY", "0.0")
                do! fixture.SetBotSetting("BURST_MSGS", "3")
                do! fixture.SetBotSetting("BURST_SPEAKERS", "2")
                do! fixture.ReloadSettings()
                do! fixture.SetAzureLlmScript [||]

                let userA = Tg.user(id = 92041L, username = "zero_a", firstName = "A")
                let userB = Tg.user(id = 92042L, username = "zero_b", firstName = "B")
                for u in [ userA; userB; userA ] do
                    let! r = fixture.SendUpdate(Tg.groupMessage("обычное сообщение в потоке", u, chatId))
                    r.EnsureSuccessStatusCode() |> ignore

                do! Task.Delay 2000
                let! llmCalls = fixture.GetAzureLlmCalls()
                Assert.Empty llmCalls
                let! sends = fixture.GetFakeCalls("sendMessage")
                Assert.Empty(sends |> Array.filter (isToChat chatId))
            finally
                restoreDefaults () |> ignore
        }

    // ── Meme reactions ───────────────────────────────────────────────────────

    [<Fact>]
    let ``p=1.0 with a scripted react action sets a message reaction`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()
            try
                do! fixture.SetBotSetting("MEME_REACT_PROBABILITY", "1.0")
                do! fixture.ReloadSettings()

                let user = Tg.user(id = 93001L, username = "meme_react", firstName = "Meme")
                let fileId = "meme-react-photo-1"
                do! fixture.SetTelegramFile(fileId, [| 1uy; 2uy; 3uy |])
                do! fixture.SetAzureLlmScript
                        [| scripted 200 (nonStreamCompletionBody """{"action":"react","emoji":"🔥"}""") |]

                let update = Tg.groupPhotoMessage(user, fixture.TargetChatId, fileId = fileId)
                let msgId = update.Message.Value.MessageId
                let! resp = fixture.SendUpdate(update)
                resp.EnsureSuccessStatusCode() |> ignore

                let! reactions = awaitReactions 1 8000
                Assert.NotEmpty reactions
                use doc = JsonDocument.Parse(reactions[0].Body: string)
                Assert.Equal(int64 msgId, doc.RootElement.GetProperty("message_id").GetInt64())
                let reactionArr = doc.RootElement.GetProperty("reaction")
                Assert.Equal("🔥", reactionArr[0].GetProperty("emoji").GetString())
            finally
                restoreDefaults () |> ignore
        }

    [<Fact>]
    let ``p=1.0 with a scripted pass action does nothing`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()
            try
                do! fixture.SetBotSetting("MEME_REACT_PROBABILITY", "1.0")
                do! fixture.ReloadSettings()

                let user = Tg.user(id = 93002L, username = "meme_pass", firstName = "Meme")
                let fileId = "meme-pass-photo-1"
                do! fixture.SetTelegramFile(fileId, [| 4uy; 5uy; 6uy |])
                do! fixture.SetAzureLlmScript [| scripted 200 (nonStreamCompletionBody """{"action":"pass"}""") |]

                let update = Tg.groupPhotoMessage(user, fixture.TargetChatId, fileId = fileId)
                let msgId = update.Message.Value.MessageId
                let! resp = fixture.SendUpdate(update)
                resp.EnsureSuccessStatusCode() |> ignore

                // Prove the LLM call landed (the pass branch was exercised) then prove
                // absence of any resulting reaction/reply.
                let! llmCalls =
                    task {
                        let deadline = DateTime.UtcNow + TimeSpan.FromSeconds 8.
                        let mutable calls = [||]
                        while calls.Length = 0 && DateTime.UtcNow < deadline do
                            let! c = fixture.GetAzureLlmCalls()
                            calls <- c
                            if calls.Length = 0 then do! Task.Delay 150
                        return calls
                    }
                Assert.NotEmpty llmCalls

                let! reactions = fixture.GetFakeCalls("setMessageReaction")
                Assert.Empty reactions
                // The photo itself was never triggered, so the only sendMessage calls to
                // this chat would have to come from a meme "comment" — there are none
                // scripted here.
                let! allSends = fixture.GetFakeCalls("sendMessage")
                Assert.Empty(allSends |> Array.filter (isToChat fixture.TargetChatId))
                ignore msgId
            finally
                restoreDefaults () |> ignore
        }

    [<Fact>]
    let ``malformed JSON is treated as pass — no crash, subsequent messages still work`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()
            try
                do! fixture.SetBotSetting("MEME_REACT_PROBABILITY", "1.0")
                do! fixture.ReloadSettings()

                let user = Tg.user(id = 93003L, username = "meme_bad", firstName = "Meme")
                let fileId = "meme-malformed-photo-1"
                do! fixture.SetTelegramFile(fileId, [| 7uy; 8uy; 9uy |])
                do! fixture.SetAzureLlmScript [| scripted 200 (nonStreamCompletionBody "not actually json at all") |]

                let update = Tg.groupPhotoMessage(user, fixture.TargetChatId, fileId = fileId)
                let! resp = fixture.SendUpdate(update)
                resp.EnsureSuccessStatusCode() |> ignore

                do! Task.Delay 2000
                let! reactions = fixture.GetFakeCalls("setMessageReaction")
                Assert.Empty reactions

                let! logs = fixture.GetBotLogs()
                Assert.Contains("malformed JSON", logs)

                // No crash: a completely unrelated triggered message right after still
                // gets a normal reply (proves the chat lock/handler didn't get stuck).
                do! fixture.SetAzureLlmScript [| scripted 200 (nonStreamCompletionBody "ok") |]
                let mention = $"@{fixture.BotUsername}"
                let text = $"{mention} всё ещё живая?"
                let entities = [| MessageEntity.Create(``type`` = "mention", offset = 0L, length = int64 mention.Length) |]
                let triggerUpdate = Tg.quickMsg(text = text, chat = Tg.chat(id = fixture.TargetChatId), from = user, entities = entities)
                let triggerMsgId = triggerUpdate.Message.Value.MessageId
                let! triggerResp = fixture.SendUpdate(triggerUpdate)
                triggerResp.EnsureSuccessStatusCode() |> ignore

                let! sends = awaitSendsToChat fixture.TargetChatId 1 8000
                ignore triggerMsgId
                Assert.NotEmpty sends
            finally
                restoreDefaults () |> ignore
        }

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            ValueTask(task {
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()
                do! restoreDefaults ()
                do! fixture.SetBotSetting("RESPONDER_MODE", "llm")
                do! fixture.ReloadSettings()
            } :> Task)

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(task {
                do! restoreDefaults ()
                do! fixture.SetBotSetting("RESPONDER_MODE", "echo")
                do! fixture.ReloadSettings()
            } :> Task)
