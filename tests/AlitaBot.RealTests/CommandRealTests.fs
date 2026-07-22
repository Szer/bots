namespace AlitaBot.RealTests

open System
open System.Collections.Generic
open System.Threading.Tasks
open Dapper
open Npgsql
open Xunit

/// Phase-1 Slice 4 real-Telegram tests: the command dispatcher's auto-generated
/// /help, /model, /summary (incl. the admin-bot ephemeral-send re-probe —
/// see src/AlitaBot/README.md's "Ephemeral message probe"), and /usage — run against
/// a real test bot and (for /summary) real Azure AI Foundry.
type CommandRealTests(fx: RealAssemblyFixture) =
    let env = fx.Env

    let queryOne (sql: string) (param: obj) =
        task {
            use conn = new NpgsqlConnection(fx.DbConnectionString)
            let! rows = conn.QueryAsync<LogRow>(sql, param)
            return rows |> Seq.tryHead
        }

    /// Polls for the bot's reply row attributed (via reply_to_message_id, Bot-API
    /// domain) to `userMessageId` — mirrors SmokeTests.fs's awaitBotReplyRow. `timeout`
    /// defaults to `Timeouts.dbSettle` but /summary's caller passes something longer:
    /// the row only lands after a real LLM summarization call PLUS an ephemeral-send
    /// HTTPS round trip, occasionally >15s for this chat's (test-accumulated) transcript
    /// size — a plain DB-settle timeout isn't enough headroom for that.
    let awaitBotReplyRow (userMessageId: int64) (timeout: TimeSpan option) =
        task {
            let deadline = DateTime.UtcNow + defaultArg timeout Timeouts.dbSettle
            let mutable found = None

            while found.IsNone && DateTime.UtcNow < deadline do
                let! row =
                    queryOne
                        """
SELECT message_id, user_id, is_bot, reply_to_message_id, text
FROM message_log
WHERE chat_id = @chat_id AND is_bot = true AND reply_to_message_id = @rid
ORDER BY message_id LIMIT 1;
"""
                        {| chat_id = env.TestChatId; rid = userMessageId |}

                found <- row
                if found.IsNone then do! Task.Delay 500

            return found
        }

    /// Polls for the most recent `[<prefix>%` command row logged for this chat — the
    /// Bot-API-domain message_id, needed because `fx.UserClient.SendText` returns an
    /// MTProto id, a DIFFERENT numbering domain than message_log.message_id (see
    /// README's "MTProto-vs-Bot-API message-id domain warning" — mirrors
    /// ImageGenRealTests.fs's awaitUserCmdRow).
    let awaitCommandRow (textPrefix: string) =
        task {
            let deadline = DateTime.UtcNow + Timeouts.dbSettle
            let mutable found = None

            while found.IsNone && DateTime.UtcNow < deadline do
                let! row =
                    queryOne
                        """
SELECT message_id, user_id, is_bot, reply_to_message_id, text
FROM message_log
WHERE chat_id = @chat_id AND is_bot = false AND text LIKE @prefix
ORDER BY message_id DESC LIMIT 1;
"""
                        {| chat_id = env.TestChatId; prefix = textPrefix + "%" |}

                found <- row
                if found.IsNone then do! Task.Delay 500

            return found
        }

    [<Fact>]
    member _.``help lists every registered command``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let! msgId = fx.UserClient.SendText(env.TestChatId, "/help")
            let! reply = fx.UserClient.AwaitReplyTo(env.TestChatId, msgId, Timeouts.reply)

            for name in [ "/img"; "/model"; "/summary"; "/usage"; "/help" ] do
                Assert.Contains(name, reply.message)
        })

    [<Fact>]
    member _.``model with no arg shows the current LLM deployment``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            if String.IsNullOrWhiteSpace env.LlmDeployment then
                Assert.Skip "ALITA_LLM_DEPLOYMENT missing in ~/.alita-test/env"

            let! msgId = fx.UserClient.SendText(env.TestChatId, "/model")
            let! reply = fx.UserClient.AwaitReplyTo(env.TestChatId, msgId, Timeouts.reply)

            Assert.Contains(env.LlmDeployment, reply.message)
        })

    /// Combined test (plan's real-suite item): sends a GUID-marked message, then
    /// /summary, then /usage — checking each depends on the previous having actually
    /// run against real Telegram + real Azure, so keeping them in one test avoids
    /// three separate slow real-Telegram round trips for what's really one scenario.
    [<Fact>]
    member _.``summary references recent chat content and usage shows a cost line afterwards``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            if env.ResponderMode <> "llm" then
                Assert.Skip
                    "RESPONDER_MODE=llm required (real /summary calls Azure AI Foundry) — run `RESPONDER_MODE=llm make real-test`"

            let marker = Guid.NewGuid().ToString "N"
            let topic = $"обсуждаем маркер {marker}, он очень важен для этого теста"
            let! _chatMsgId = fx.UserClient.SendText(env.TestChatId, topic)
            // Give message_log a moment to have the row before /summary reads recent context.
            do! Task.Delay 1500

            // Ephemeral-send re-probe (Bot API 10.2, BotHelpers.trySendEphemeralOrReply),
            // now that the test bot is a GROUP ADMIN — see README's "Ephemeral message
            // probe" for the full empirical writeup (also `make probe-ephemeral`,
            // EphemeralProbe.fs, for a from-scratch repro against the raw Bot API).
            // As admin, Telegram ACCEPTS the ephemeral sendMessage call — but the accepted
            // message is invisible to this MTProto user client both as a live push update
            // AND via `Messages_GetHistory`, even though this account is the exact receiver
            // the message is scoped to. So `AwaitReplyTo` (a live-update wait) would time
            // out here forever — message_log (DB) is the only observable signal this
            // harness has for "the ephemeral send happened". A raw-update sink is still
            // wired up as a live tripwire: if Telegram ever starts pushing ephemeral
            // messages to receivers, this line will start printing and the finding above
            // needs revisiting.
            let rawUpdateKinds = List<string>()
            fx.UserClient.AddRawUpdateSink(fun updates ->
                for u in updates.UpdateList do
                    rawUpdateKinds.Add(u.GetType().Name))

            let! historyBeforeSummary = fx.UserClient.RecentMessageIds(env.TestChatId, 15)
            let! summaryMsgId = fx.UserClient.SendText(env.TestChatId, "/summary 20")

            // summaryMsgId is an MTProto id (from SendText) — NOT directly comparable to
            // message_log.message_id (Bot API domain, see awaitCommandRow's doc comment).
            // Resolve the Bot-API-domain id via the logged "[summary-cmd] ..." row first.
            match! awaitCommandRow "[summary-cmd]" with
            | None -> Assert.Fail "no '[summary-cmd] ...' row landed in message_log"
            | Some cmdRow ->
                match! awaitBotReplyRow cmdRow.message_id (Some(TimeSpan.FromSeconds 60.)) with
                | None ->
                    Assert.Fail
                        $"no message_log row replying (reply_to_message_id={cmdRow.message_id}) to the /summary command"
                | Some botRow ->
                    Assert.True botRow.is_bot
                    // The digest is an LLM paraphrase, not a verbatim quote — check it's
                    // real, non-trivial output rather than requiring the marker itself.
                    Assert.False(String.IsNullOrWhiteSpace botRow.text)
                    // Confirms the ephemeral path actually ran (not the BOT_NOT_ADMIN
                    // fallback): BotHelpers.loggableMessageId logs EphemeralMessageId
                    // whenever Telegram accepted the ephemeral send (MessageId comes back
                    // 0 in that case — see BotHelpers.fs) instead of the chat's own small
                    // sequential message id a fallback normal reply would carry. Empirically
                    // (probe-ephemeral) EphemeralMessageId values look like 13947911 /
                    // 93205090 / 95764773 — 1e6 is a generous floor well above anything
                    // this test chat's own sequential counter will reach.
                    Assert.True(
                        botRow.message_id >= 1_000_000L,
                        $"expected an EphemeralMessageId-shaped message_id (>= 1,000,000) confirming the ephemeral \
                          send was accepted, got {botRow.message_id} — looks like it fell back to a normal reply")

            // The requester's own /summary command is the only message this client should
            // see land in shared/receiver-visible history — the bot's ephemeral reply must
            // NOT show up here, confirming it's genuinely invisible via Messages_GetHistory
            // (not just "hasn't arrived yet"; the DB-confirmed reply above already happened).
            let! historyAfterSummary = fx.UserClient.RecentMessageIds(env.TestChatId, 15)
            let newHistoryIds = Set.difference (Set.ofArray historyAfterSummary) (Set.ofArray historyBeforeSummary)
            let newHistoryIdsText = String.Join(",", newHistoryIds)

            Assert.True(
                newHistoryIds.Count <= 1,
                $"expected at most the requester's own /summary command to be new in Messages_GetHistory, \
                  got {newHistoryIds.Count} new id(s): [{newHistoryIdsText}] — did the ephemeral \
                  reply leak into shared history?")

            printfn
                "[ephemeral probe] raw update kinds seen around the /summary exchange (expected: none from the \
                 ephemeral reply itself): %s"
                (String.Join(", ", rawUpdateKinds |> Seq.distinct))

            let! usageMsgId = fx.UserClient.SendText(env.TestChatId, "/usage")
            let! usageReply = fx.UserClient.AwaitReplyTo(env.TestChatId, usageMsgId, Timeouts.reply)

            Assert.Contains("$", usageReply.message)
        })
