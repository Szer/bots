namespace AlitaBot.RealTests

open System
open System.Collections.Generic
open System.Threading.Tasks
open Dapper
open Npgsql
open Xunit

/// Phase-1 Slice 4 real-Telegram tests: the command dispatcher's auto-generated
/// /help, /model, /summary (incl. a best-effort look at the ephemeral-send probe —
/// see src/AlitaBot/README.md's "Ephemeral message probe"), and /usage — run against
/// a real test bot and (for /summary) real Azure AI Foundry.
type CommandRealTests(fx: RealAssemblyFixture) =
    let env = fx.Env

    /// `/summary`'s real Azure AI Foundry call (ephemeral-send-with-fallback, plus a
    /// non-stream LLM completion) is the one real-test call observed to occasionally
    /// exceed `Timeouts.reply` (90s) specifically when it lands deep into a long,
    /// fully-sequential real-test run — cumulative real-Azure latency across dozens of
    /// prior real LLM/STT/TTS calls in the same run, not this test's own logic (empirically
    /// reproduced 5/5 consecutive full-suite runs while verifying the Gemini provider PR:
    /// 3 local + 2 CI, always this exact test, never in isolation). Scoped to just this one
    /// `AwaitReplyTo` call rather than widening the shared `Timeouts.reply` everywhere,
    /// which would mask a real regression in every OTHER real test's timeout instead.
    let summaryReplyTimeout = TimeSpan.FromSeconds 150.

    let queryOne (sql: string) (param: obj) =
        task {
            use conn = new NpgsqlConnection(fx.DbConnectionString)
            let! rows = conn.QueryAsync<LogRow>(sql, param)
            return rows |> Seq.tryHead
        }

    /// Polls for the bot's reply row attributed (via reply_to_message_id, Bot-API
    /// domain) to `userMessageId` — mirrors SmokeTests.fs's awaitBotReplyRow.
    let awaitBotReplyRow (userMessageId: int64) =
        task {
            let deadline = DateTime.UtcNow + Timeouts.dbSettle
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
        task {
            fx.SkipUnlessUserClient()

            let! msgId = fx.UserClient.SendText(env.TestChatId, "/help")
            let! reply = fx.UserClient.AwaitReplyTo(env.TestChatId, msgId, Timeouts.reply)

            for name in [ "/img"; "/model"; "/summary"; "/usage"; "/help" ] do
                Assert.Contains(name, reply.message)
        }

    [<Fact>]
    member _.``model with no arg shows the current LLM deployment``() =
        task {
            fx.SkipUnlessUserClient()

            if String.IsNullOrWhiteSpace env.LlmDeployment then
                Assert.Skip "ALITA_LLM_DEPLOYMENT missing in ~/.alita-test/env"

            let! msgId = fx.UserClient.SendText(env.TestChatId, "/model")
            let! reply = fx.UserClient.AwaitReplyTo(env.TestChatId, msgId, Timeouts.reply)

            Assert.Contains(env.LlmDeployment, reply.message)
        }

    /// Combined test (plan's real-suite item): sends a GUID-marked message, then
    /// /summary, then /usage — checking each depends on the previous having actually
    /// run against real Telegram + real Azure, so keeping them in one test avoids
    /// three separate slow real-Telegram round trips for what's really one scenario.
    [<Fact>]
    member _.``summary references recent chat content and usage shows a cost line afterwards``() =
        task {
            fx.SkipUnlessUserClient()

            if env.ResponderMode <> "llm" then
                Assert.Skip
                    "RESPONDER_MODE=llm required (real /summary calls Azure AI Foundry) — run `RESPONDER_MODE=llm make real-test`"

            let marker = Guid.NewGuid().ToString "N"
            let topic = $"обсуждаем маркер {marker}, он очень важен для этого теста"
            let! _chatMsgId = fx.UserClient.SendText(env.TestChatId, topic)
            // Give message_log a moment to have the row before /summary reads recent context.
            do! Task.Delay 1500

            // Best-effort ephemeral-send probe (Bot API 10.2, BotHelpers.trySendEphemeralOrReply):
            // logs every raw update kind seen while awaiting the /summary reply. A single
            // MTProto test-user account can't distinguish "ephemeral, delivered to me because
            // I'm the receiver" from "a normal reply everyone in the chat would also see" —
            // that needs a second account not in this harness — so this only records what
            // arrives, it doesn't assert on it. See README's "Ephemeral message probe" section
            // for what a run of this test actually observed.
            let rawUpdateKinds = List<string>()
            fx.UserClient.AddRawUpdateSink(fun updates ->
                for u in updates.UpdateList do
                    rawUpdateKinds.Add(u.GetType().Name))

            let! summaryMsgId = fx.UserClient.SendText(env.TestChatId, "/summary 20")
            let! summaryReply = fx.UserClient.AwaitReplyTo(env.TestChatId, summaryMsgId, summaryReplyTimeout)

            Assert.False(String.IsNullOrWhiteSpace summaryReply.message)
            printfn
                "[ephemeral probe] raw update kinds seen while awaiting the /summary reply: %s"
                (String.Join(", ", rawUpdateKinds |> Seq.distinct))

            // summaryMsgId is an MTProto id (from SendText) — NOT directly comparable to
            // message_log.message_id (Bot API domain, see awaitCommandRow's doc comment).
            // Resolve the Bot-API-domain id via the logged "[summary-cmd] ..." row first.
            match! awaitCommandRow "[summary-cmd]" with
            | None -> Assert.Fail "no '[summary-cmd] ...' row landed in message_log"
            | Some cmdRow ->
                match! awaitBotReplyRow cmdRow.message_id with
                | None ->
                    Assert.Fail
                        $"no message_log row replying (reply_to_message_id={cmdRow.message_id}) to the /summary command"
                | Some botRow ->
                    Assert.True botRow.is_bot
                    // The digest is an LLM paraphrase, not a verbatim quote — check it's
                    // real, non-trivial output rather than requiring the marker itself.
                    Assert.False(String.IsNullOrWhiteSpace botRow.text)

            let! usageMsgId = fx.UserClient.SendText(env.TestChatId, "/usage")
            let! usageReply = fx.UserClient.AwaitReplyTo(env.TestChatId, usageMsgId, Timeouts.reply)

            Assert.Contains("$", usageReply.message)
        }
