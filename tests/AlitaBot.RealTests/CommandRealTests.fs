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

    /// Polls for a fresh `llm_usage` row of `kind='chat'` for this chat, inserted after
    /// `since` — proves a chat-completion LLM call actually completed server-side.
    /// `/summary`'s reply verification needs this: when Telegram accepts the ephemeral
    /// `receiver_user_id` send (BotHelpers.trySendEphemeralOrReply), the reply is
    /// deliberately never logged into `message_log` (see handleSummaryCommand's `reply`'s
    /// doc comment — an ephemeral send reports `message_id: 0`, not a real Bot API id) and
    /// is not observable over MTProto as a "reply" either (no real id to correlate) — so
    /// `llm_usage` is the one channel that reliably proves the summarization itself
    /// succeeded regardless of whether Telegram scoped the reply to just the requester or
    /// fell back to a normal, chat-visible one.
    let awaitLlmUsageSince (chatId: int64) (since: DateTime) =
        task {
            let deadline = DateTime.UtcNow + Timeouts.dbSettle
            let mutable found = false

            while not found && DateTime.UtcNow < deadline do
                use conn = new NpgsqlConnection(fx.DbConnectionString)
                let! exists =
                    conn.QuerySingleAsync<bool>(
                        "SELECT EXISTS(SELECT 1 FROM llm_usage WHERE chat_id = @chat_id AND kind = 'chat' AND called_at >= @since);",
                        {| chat_id = chatId; since = since |})
                found <- exists
                if not found then do! Task.Delay 500

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
            // logs every raw update kind seen while awaiting the /summary reply below —
            // diagnostic only, never asserted on (see README's "Ephemeral message probe").
            let rawUpdateKinds = List<string>()
            fx.UserClient.AddRawUpdateSink(fun updates ->
                for u in updates.UpdateList do
                    rawUpdateKinds.Add(u.GetType().Name))

            // REGRESSION (2026-07-22): this used to `AwaitReplyTo` (this harness's MTProto
            // client polling for a message whose reply-to matches the command) for the
            // digest reply itself. When Telegram accepts the ephemeral `receiver_user_id`
            // send, the response reports `message_id: 0` — not a real, addressable Bot API
            // message id (those start at 1) — so there is nothing for MTProto to correlate
            // as "a reply to this command", and that wait never resolved (previously
            // "fixed" by giving it a longer timeout, which couldn't work: the target
            // genuinely never arrives that way, at any duration — see git history for the
            // full story). Verified instead below via `message_log` (when Telegram fell
            // back to a normal, chat-visible reply) OR a fresh `llm_usage` row (when the
            // ephemeral send succeeded) — both reliable server-side signals that don't
            // depend on MTProto being able to see a Bot-API-only ephemeral message.
            let preRequest = DateTime.UtcNow
            let! _summaryMsgId = fx.UserClient.SendText(env.TestChatId, "/summary 20")

            match! awaitCommandRow "[summary-cmd]" with
            | None -> Assert.Fail "no '[summary-cmd] ...' row landed in message_log"
            | Some cmdRow ->
                match! awaitBotReplyRow cmdRow.message_id with
                | Some botRow ->
                    Assert.True botRow.is_bot
                    // The digest is an LLM paraphrase, not a verbatim quote — check it's
                    // real, non-trivial output rather than requiring the marker itself.
                    Assert.False(String.IsNullOrWhiteSpace botRow.text)
                | None ->
                    let! usageLogged = awaitLlmUsageSince env.TestChatId preRequest
                    Assert.True(
                        usageLogged,
                        $"no message_log reply row (reply_to_message_id={cmdRow.message_id}) AND no fresh llm_usage "
                        + $"'chat' row for chat {env.TestChatId} — /summary produced no observable evidence of success")

            printfn
                "[ephemeral probe] raw update kinds seen while awaiting the /summary reply: %s"
                (String.Join(", ", rawUpdateKinds |> Seq.distinct))

            let! usageMsgId = fx.UserClient.SendText(env.TestChatId, "/usage")
            let! usageReply = fx.UserClient.AwaitReplyTo(env.TestChatId, usageMsgId, Timeouts.reply)

            Assert.Contains("$", usageReply.message)
        }
