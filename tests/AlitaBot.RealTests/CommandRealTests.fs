namespace AlitaBot.RealTests

open System
open System.Threading.Tasks
open Xunit

/// Phase-1 Slice 4 real-Telegram tests: the command dispatcher's auto-generated
/// /help, /model, /summary, and /usage — run against a real test bot and (for
/// /summary) real Azure AI Foundry. /summary used to send its reply via Bot API 10.2's
/// ephemeral `sendMessage`; retired after staging feedback found it invisible in
/// practice (see src/AlitaBot/README.md's "Ephemeral message probe [RETIRED]") — its
/// reply is now a normal, whole-chat-visible reply like every other command here, so
/// the test below observes it the same way `/help`/`/model`/`/usage` do (AwaitReplyTo,
/// a live MTProto update wait) instead of polling message_log.
type CommandRealTests(fx: RealAssemblyFixture) =
    let env = fx.Env

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
    member _.``model with no arg does not leak the Azure deployment id``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            if String.IsNullOrWhiteSpace env.LlmDeployment then
                Assert.Skip "ALITA_LLM_DEPLOYMENT missing in ~/.alita-test/env"

            let! msgId = fx.UserClient.SendText(env.TestChatId, "/model")
            let! reply = fx.UserClient.AwaitReplyTo(env.TestChatId, msgId, Timeouts.reply)

            // /model's LLM_MODELS bot_setting maps the deployment id to a real model name
            // for display (see BotService.handleModelCommand) — the raw deployment id
            // (this bot's Azure Foundry namespacing convention, e.g. "alita-gpt-5-mini")
            // must never appear in the reply. This harness has no direct view of the
            // configured LLM_MODELS catalog to assert the exact display name against, so
            // it checks the negative (no deployment-id leak) plus that /model actually
            // said something.
            Assert.False(String.IsNullOrWhiteSpace reply.message)
            Assert.DoesNotContain(env.LlmDeployment, reply.message)
        })

    /// Combined test (plan's real-suite item): sends a GUID-marked message, then
    /// /summary, then /usage — checking each depends on the previous having actually
    /// run against real Telegram + real Azure, so keeping them in one test avoids
    /// three separate slow real-Telegram round trips for what's really one scenario.
    /// Acceptance criterion for the ephemeral-reply retirement (staging feedback:
    /// "ephemeral messages are not useful"): `/summary`'s reply must be a NORMAL,
    /// whole-chat-visible reply — asserted here the same way as every other command
    /// (`AwaitReplyTo`, a live-update wait), which only works because it's no longer
    /// an ephemeral send that Telegram accepts but never actually delivers.
    [<Fact>]
    member _.``summary replies visibly with recent chat content and usage shows a cost line afterwards``() =
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

            let! summaryMsgId = fx.UserClient.SendText(env.TestChatId, "/summary 20")
            let! summaryReply = fx.UserClient.AwaitReplyTo(env.TestChatId, summaryMsgId, TimeSpan.FromSeconds 60.)

            // The digest is an LLM paraphrase, not a verbatim quote — check it's real,
            // non-trivial output rather than requiring the marker itself.
            Assert.False(String.IsNullOrWhiteSpace summaryReply.message)

            let! usageMsgId = fx.UserClient.SendText(env.TestChatId, "/usage")
            let! usageReply = fx.UserClient.AwaitReplyTo(env.TestChatId, usageMsgId, Timeouts.reply)

            Assert.Contains("$", usageReply.message)
        })
