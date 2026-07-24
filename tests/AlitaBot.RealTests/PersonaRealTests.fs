namespace AlitaBot.RealTests

open System
open System.Net.Http
open System.Text.RegularExpressions
open System.Threading.Tasks
open Dapper
open Npgsql
open TL
open Xunit

/// Persona-consistency-pass banned-pattern detectors, shared between the general "no
/// assistant-isms" smoke test and the factual-question anti-worksheet test below. Each
/// pattern is anchored to avoid false-positiving on legitimate uses of the same words
/// mid-reply (see each detector's own doc comment for its specific anchor reasoning).
module private PersonaChecks =

    /// Closing-invitation offers ("Хочешь — уточню", "Если нужно, посчитаю", "скинь
    /// данные — посчитаю", "дай знать", "обращайся, если что") are only an assistant-ism
    /// as a REPLY ENDING — Alita quoting someone else saying "обращайся" mid-reply, or
    /// using "хочешь" as part of an unrelated sentence, must not trip this. Anchored to
    /// the final sentence, not the whole text.
    let private closingInvitationPattern =
        Regex(@"(?i)\b(хочешь|если\s+(хочешь|нужно|надо)|скинь|дай\s+знать|обращайся)\b", RegexOptions.Compiled)

    let private lastSentence (text: string) =
        let parts = text.Split([| '.'; '!'; '?'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
        if parts.Length = 0 then "" else parts[parts.Length - 1].Trim()

    let hasClosingInvitation (text: string) = closingInvitationPattern.IsMatch(lastSentence text)

    /// Formula-worksheet kits: bullet/numbered lines carrying × or = or "формул(а/ы)" —
    /// the calibration failure mode (plan §Novogrudok example) where a factual question
    /// gets a "Формула: годовая генерация = мощность × 8760 × коэффициент…" list instead
    /// of a narrative answer. Anchored to bullet/numbered LINES so a bare "2+2=4" aside
    /// mid-paragraph doesn't trip it.
    let private formulaBulletPattern =
        Regex(@"(?im)^\s*(?:[-•*]|\d+[.)])\s+.*(?:×|=|формул).*$", RegexOptions.Compiled)

    let hasFormulaWorksheet (text: string) = formulaBulletPattern.IsMatch text

    /// Name-as-headline opener: the reply starting with just "Айрат." (or similar bare
    /// name + period) before the actual content, instead of a name woven into a live
    /// sentence. Anchored to the START of the (trimmed) reply.
    let private nameHeadlinePattern = Regex(@"^\s*Айрат\.\s", RegexOptions.Compiled)

    let hasNameHeadlineOpener (text: string) = nameHeadlinePattern.IsMatch text

/// Slice 6 real-Telegram tests: the reaction channel (redesign, PR #253 follow-up —
/// `REACTION_PROBABILITY`, independent of replying), MarkdownV2 entities on the settled
/// final message, and a persona smoke check (no assistant-isms leak into a real LLM
/// reply). All need `RESPONDER_MODE=llm` — MDV2 rendering only ever runs for the "llm"
/// ResponderService path, and the persona check is obviously meaningless against "echo"
/// mode's `pong: ...` replies.
type PersonaRealTests(fx: RealAssemblyFixture) =
    let env = fx.Env

    let requireLlmMode () =
        if env.ResponderMode <> "llm" then
            Assert.Skip "RESPONDER_MODE=llm required — run `RESPONDER_MODE=llm make real-test`"

    /// Upserts a `bot_setting` row directly (same shape as DevDb's own upsert) — used to
    /// flip REACTION_PROBABILITY/etc. for the duration of one test, mirroring the fake
    /// suite's `fixture.SetBotSetting` + `/reload-settings` idiom (ContainerTestBase.fs).
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

    /// Restores the reaction channel to its off-by-default posture — every test below
    /// (not just the reaction-channel one) calls this defensively so it never depends on
    /// declaration/execution order relative to whichever test last flipped it.
    let restoreDefaultWeights () =
        task {
            do! setBotSetting "REACTION_PROBABILITY" "0.0"
            do! reloadSettings ()
        }

    // ── (a) Reaction channel: BOTH a reaction AND a reply, independently ─────

    [<Fact>]
    member _.``REACTION_PROBABILITY=1.0 makes the bot react AND still reply — the two channels don't interfere``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()
            requireLlmMode ()

            try
                // "random" so the reply's own LLM call is the only LLM call in play here —
                // an "llm" emoji pick would be a second, independent call.
                do! setBotSetting "REACTION_PROBABILITY" "1.0"
                do! setBotSetting "REACTION_CHOICE_MODE" "random"
                do! reloadSettings ()

                let marker = Guid.NewGuid().ToString "N"
                let! msgId = fx.UserClient.SendText(env.TestChatId, $"@{env.BotUsername} привет как дела {marker}")

                match! fx.UserClient.TryAwaitReactionOn(env.TestChatId, msgId, TimeSpan.FromSeconds 30.) with
                | None ->
                    Assert.Fail
                        "expected message_id to carry a reaction (via Messages_GetHistory polling — see TgUserClient.TryAwaitReactionOn's doc comment) within 30s"
                | Some emojis -> Assert.NotEmpty emojis

                // A TRIGGERED (mentioned) message always replies now — the reaction above
                // is an independent channel, not a substitute for the reply path.
                let! reply = fx.UserClient.TryAwaitReplyTo(env.TestChatId, msgId, Timeouts.reply)
                Assert.True(reply.IsSome, "expected a text reply alongside the reaction — the two channels must not interfere")
            with ex ->
                // MUST be awaited, not fire-and-forget: a later test in this class (or a
                // re-run) can start before an un-awaited restore lands and itself gets an
                // unwanted reaction from the stale probability — exactly what happened once
                // while developing this test (back when this flipped OUTCOME_WEIGHTS).
                // Re-raises so the original failure (if any) is still what fails the test,
                // not swallowed by the cleanup.
                do! restoreDefaultWeights ()
                raise ex

            do! restoreDefaultWeights ()
        })

    // ── (b) MarkdownV2: settled final message carries real entities ──────────

    [<Fact>]
    member _.``a markdown-shaped reply settles with non-empty entities (best-effort)``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()
            requireLlmMode ()
            // Defensive: don't depend on declaration/execution order relative to the
            // reaction-channel test above — ensure "reply" is actually possible here too.
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
        })

    // ── (c) Persona smoke: no assistant-isms ─────────────────────────────────

    [<Fact>]
    member _.``a real reply contains no assistant-isms``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()
            requireLlmMode ()
            // Defensive: don't depend on declaration/execution order relative to the
            // reaction-channel test above — ensure "reply" is actually possible here too.
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

            Assert.False(
                PersonaChecks.hasClosingInvitation finalText,
                $"expected no closing-invitation offer ending the persona reply: {finalText}")
            Assert.False(
                PersonaChecks.hasNameHeadlineOpener finalText,
                $"expected no name-headline opener in the persona reply: {finalText}")
            Assert.False(
                PersonaChecks.hasFormulaWorksheet finalText,
                $"expected no formula-worksheet bullet list in the persona reply: {finalText}")
        })

    // ── (d) Persona: factual question gets a narrative answer, not a worksheet ───────

    /// Calibration-example regression test (see plan §Novogrudok example): a factual
    /// "почему…" question must get confident narrative knowledge with a point of view —
    /// never a methodology/formula worksheet, never a closing invitation to send more
    /// data, never a bare "Айрат." name-headline opener.
    [<Fact>]
    member _.``a factual question gets a narrative answer, not a formula worksheet``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()
            requireLlmMode ()
            do! restoreDefaultWeights ()

            let marker = Guid.NewGuid().ToString "N"
            let! msgId =
                fx.UserClient.SendText(
                    env.TestChatId,
                    $"@{env.BotUsername} почему в Новогрудках так много возобновляемой энергии? {marker}")
            let! reply = fx.UserClient.AwaitReplyTo(env.TestChatId, msgId, Timeouts.reply)
            let! finalText = fx.UserClient.AwaitEditsSettled(env.TestChatId, reply.id, Timeouts.editQuiet)

            Assert.False(String.IsNullOrWhiteSpace finalText)
            Assert.False(
                PersonaChecks.hasFormulaWorksheet finalText,
                $"expected a narrative answer with a point of view, not a formula worksheet: {finalText}")
            Assert.False(
                PersonaChecks.hasClosingInvitation finalText,
                $"expected no closing-invitation offer ending the reply: {finalText}")
            Assert.False(
                PersonaChecks.hasNameHeadlineOpener finalText,
                $"expected no name-headline opener: {finalText}")
        })
