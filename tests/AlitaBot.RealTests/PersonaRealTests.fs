namespace AlitaBot.RealTests

open System
open System.Net.Http
open System.Threading.Tasks
open Dapper
open Npgsql
open TL
open Xunit

/// Slice 6 real-Telegram tests: outcome-router emoji reactions, MarkdownV2 entities on
/// the settled final message, and a persona smoke check (no assistant-isms leak into a
/// real LLM reply). All three need `RESPONDER_MODE=llm` — the outcome router and MDV2
/// rendering only ever run for the "llm" ResponderService path, and the persona check is
/// obviously meaningless against "echo" mode's `pong: ...` replies.
type PersonaRealTests(fx: RealAssemblyFixture) =
    let env = fx.Env

    let requireLlmMode () =
        if env.ResponderMode <> "llm" then
            Assert.Skip "RESPONDER_MODE=llm required — run `RESPONDER_MODE=llm make real-test`"

    /// Upserts a `bot_setting` row directly (same shape as DevDb's own upsert) — used to
    /// flip OUTCOME_WEIGHTS/etc. for the duration of one test, mirroring the fake suite's
    /// `fixture.SetBotSetting` + `/reload-settings` idiom (ContainerTestBase.fs).
    let setBotSetting (key: string) (value: string) =
        task {
            use conn = new NpgsqlConnection(fx.DbConnectionString)
            do! conn.OpenAsync()
            //language=postgresql
            let sql =
                """
INSERT INTO bot_setting(key, value, type, feature_group)
VALUES(@key, @value, 'FREE_FORM', 'RUNTIME')
ON CONFLICT (key) DO UPDATE SET value = @value
"""
            let! _ = conn.ExecuteAsync(sql, {| key = key; value = value |})
            ()
        }

    /// Same call `RealAssemblyFixture`'s (private) remote-mode reload uses — needed here
    /// too since `setBotSetting` above writes straight to Postgres, bypassing the bot's
    /// cached `IOptions<BotConfiguration>` (same reasoning as `/model`'s `ISettingsReloader`
    /// call, just from outside the process).
    let reloadSettings () =
        task {
            use http = new HttpClient(Timeout = TimeSpan.FromSeconds 10.)
            http.DefaultRequestHeaders.Add("X-Telegram-Bot-Api-Secret-Token", env.WebhookSecret)
            let! resp = http.PostAsync(env.ReloadSettingsUrl, null)
            resp.EnsureSuccessStatusCode() |> ignore
        }

    let restoreDefaultWeights () =
        task {
            do! setBotSetting "OUTCOME_WEIGHTS" """{"reply":100,"silence":0,"emoji":0}"""
            do! reloadSettings ()
        }

    // ── (a) Outcome router: emoji reaction, no text reply ────────────────────

    [<Fact>]
    member _.``OUTCOME_WEIGHTS emoji=100 makes the bot react instead of replying``() =
        task {
            fx.SkipUnlessUserClient()
            requireLlmMode ()

            try
                do! setBotSetting "OUTCOME_WEIGHTS" """{"reply":0,"silence":0,"emoji":100}"""
                do! reloadSettings ()

                let marker = Guid.NewGuid().ToString "N"
                let! msgId = fx.UserClient.SendText(env.TestChatId, $"@{env.BotUsername} привет как дела {marker}")

                match! fx.UserClient.TryAwaitReactionOn(env.TestChatId, msgId, TimeSpan.FromSeconds 30.) with
                | None ->
                    Assert.Fail
                        "expected message_id to carry a reaction (via Messages_GetHistory polling — see TgUserClient.TryAwaitReactionOn's doc comment) within 30s"
                | Some emojis -> Assert.NotEmpty emojis

                // No text reply within 15s — the emoji outcome never calls ResponderService.
                let! reply = fx.UserClient.TryAwaitReplyTo(env.TestChatId, msgId, TimeSpan.FromSeconds 15.)
                Assert.True(reply.IsNone, "expected no text reply while OUTCOME_WEIGHTS routes to emoji")
            with ex ->
                // MUST be awaited, not fire-and-forget: a later test in this class (or a
                // re-run) can start before an un-awaited restore lands and itself gets
                // routed to "emoji" by the stale weights — exactly what happened once
                // while developing this test. Re-raises so the original failure (if any)
                // is still what fails the test, not swallowed by the cleanup.
                do! restoreDefaultWeights ()
                raise ex

            do! restoreDefaultWeights ()
        }

    // ── (b) MarkdownV2: settled final message carries real entities ──────────

    [<Fact>]
    member _.``a markdown-shaped reply settles with non-empty entities (best-effort)``() =
        task {
            fx.SkipUnlessUserClient()
            requireLlmMode ()
            // Defensive: don't depend on declaration/execution order relative to the
            // emoji-outcome test above — ensure "reply" is actually possible here too.
            do! restoreDefaultWeights ()

            let marker = Guid.NewGuid().ToString "N"
            let! msgId =
                fx.UserClient.SendText(
                    env.TestChatId,
                    $"@{env.BotUsername} ответь списком из двух пунктов с **жирным** словом {marker}")

            let! reply = fx.UserClient.AwaitReplyTo(env.TestChatId, msgId, Timeouts.reply)
            let! _finalText = fx.UserClient.AwaitEditsSettled(env.TestChatId, reply.id, Timeouts.editQuiet)
            let entities = fx.UserClient.LastEntitiesOf(env.TestChatId, reply.id)

            // Best-effort (plan §8b): the model isn't forced to actually use markdown, so
            // this can't be a hard assertion without scripting the LLM (real suite, no
            // fake) — but when it does, MDV2 -> real Telegram entities is exactly the
            // round trip Slice 6 claims to support, so log rather than silently no-op.
            if entities.Length = 0 then
                Console.WriteLine(
                    "NOTE: settled reply carried no entities — either the model didn't use markdown this time, or MDV2 rendering/parsing isn't round-tripping; not failing the test (best-effort per plan).")
            else
                Assert.Contains(entities, fun (e: MessageEntity) -> e :? MessageEntityBold)
        }

    // ── (c) Persona smoke: no assistant-isms ─────────────────────────────────

    [<Fact>]
    member _.``a real reply contains no assistant-isms``() =
        task {
            fx.SkipUnlessUserClient()
            requireLlmMode ()
            // Defensive: don't depend on declaration/execution order relative to the
            // emoji-outcome test above — ensure "reply" is actually possible here too.
            do! restoreDefaultWeights ()

            let marker = Guid.NewGuid().ToString "N"
            let! msgId = fx.UserClient.SendText(env.TestChatId, $"@{env.BotUsername} привет, ты кто? {marker}")
            let! reply = fx.UserClient.AwaitReplyTo(env.TestChatId, msgId, Timeouts.reply)
            let! finalText = fx.UserClient.AwaitEditsSettled(env.TestChatId, reply.id, Timeouts.editQuiet)

            Assert.False(String.IsNullOrWhiteSpace finalText)

            for banned in [ "как ии"; "как искусственный интеллект"; "отличный вопрос" ] do
                Assert.False(
                    finalText.Contains(banned, StringComparison.OrdinalIgnoreCase),
                    $"expected no assistant-ism '{banned}' in the persona reply: {finalText}")
        }
