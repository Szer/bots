namespace AlitaBot.RealTests

open System
open System.Net.Http
open System.Threading.Tasks
open Dapper
open Npgsql
open Xunit

/// Phase-1 Slice 8 real-Telegram tests: the morning digest job and a willingness-gated
/// interjection, both against real Azure AI Foundry. Every proactive feature defaults
/// OFF/0.0 (dev-bot-settings.sql) — each test flips only what it needs on for its own
/// duration and RESTORES every setting it touched afterward (`finally`, same "must be
/// awaited, not fire-and-forget" reasoning `PersonaRealTests`' emoji test documents: a
/// later test — or a re-run — can start before an un-awaited restore lands and inherit
/// the stale, still-live settings).
type ProactiveRealTests(fx: RealAssemblyFixture) =
    let env = fx.Env

    let requireLlmMode () =
        if env.ResponderMode <> "llm" then
            Assert.Skip "RESPONDER_MODE=llm required — run `RESPONDER_MODE=llm make real-test`"

    /// Same shape as PersonaRealTests' `setBotSetting` — upserts a `bot_setting` row
    /// directly (mirrors `DevDb`'s own upsert / the fake suite's `SetBotSetting`).
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

    let reloadSettings () =
        task {
            use http = new HttpClient(Timeout = TimeSpan.FromSeconds 10.)
            http.DefaultRequestHeaders.Add("X-Telegram-Bot-Api-Secret-Token", env.WebhookSecret)
            let! resp = http.PostAsync(env.ReloadSettingsUrl, null)
            resp.EnsureSuccessStatusCode() |> ignore
        }

    /// Counts non-bot `message_log` rows for `chatId` that share THIS bot process's
    /// current frozen "now" — used by the interjection test below to make `BURST_MSGS`
    /// account for however much traffic this persistently shared chat already has this
    /// run, instead of assuming it starts empty. Two things make a plain, unfiltered
    /// `sent_at >= now - N minutes` (mirroring `DbService.BurstStats` literally) wrong
    /// here:
    ///  1. Every real-test run leaves the bot's `TimeProvider` frozen at pod-start time
    ///     for its whole lifetime (TEST_MODE's `FakeTimeProvider` — see Program.fs — and
    ///     nothing in this test project ever calls `/test/clock/advance` the way the
    ///     hermetic suite does), so EVERY `message_log.sent_at` a given bot process
    ///     writes carries that SAME frozen instant, and `BurstStats`' own window check
    ///     (`sent_at >= frozen_now - N minutes`) is trivially true for that instant
    ///     regardless of N — using a real wall-clock `since` here would silently
    ///     undercount (eventually to 0) the longer the suite runs.
    ///  2. `make real-test`'s local Postgres volume (unlike CI's fresh-per-run AKS
    ///     Postgres) survives across separate `make real-test` invocations on the same
    ///     dev machine, so `message_log` can hold rows from SEVERAL earlier, unrelated
    ///     bot processes against this same chat id, each with its OWN distinct frozen
    ///     instant — counting ALL of them (no filter at all) over-counts just as badly,
    ///     making `BURST_MSGS` unreachable (observed: a naive unfiltered COUNT(*) of 79
    ///     when only 37 of those rows were actually from the CURRENT bot process).
    /// `MAX(sent_at)` for this chat IS the current process's frozen "now" (every row it
    /// writes carries that exact value), so anchoring the window to it — instead of to
    /// either extreme — gets exactly what `BurstStats` would see for a message arriving
    /// right now, whether the clock is frozen (today's reality) or, if that's ever fixed,
    /// live.
    let countNonBotMessagesSoFar (chatId: int64) =
        task {
            use conn = new NpgsqlConnection(fx.DbConnectionString)
            do! conn.OpenAsync()
            //language=postgresql
            let sql =
                """
SELECT COUNT(*) FROM message_log
WHERE chat_id = @chat_id AND is_bot = FALSE
  AND sent_at >= (SELECT MAX(sent_at) FROM message_log WHERE chat_id = @chat_id) - INTERVAL '5 minutes';
"""
            return! conn.QuerySingleAsync<int64>(sql, {| chat_id = chatId |})
        }

    let runJob (jobName: string) =
        task {
            use http = new HttpClient(Timeout = TimeSpan.FromSeconds 30.)
            let! resp = http.PostAsync($"{env.RunJobUrl}?name={jobName}", null)

            if not resp.IsSuccessStatusCode then
                let! body = resp.Content.ReadAsStringAsync()
                failwith $"POST {env.RunJobUrl}?name={jobName} -> {int resp.StatusCode}: {body}"
        }

    /// Same "poll scheduled_job.last_completed_at past a pre-request snapshot" pattern as
    /// DossierRealTests' `awaitJobCompletion` — proves the fire-and-forget job actually
    /// finished on the bot side, not just that the POST was accepted.
    let awaitJobCompletion (jobName: string) (preRequest: DateTime) =
        task {
            let deadline = DateTime.UtcNow + TimeSpan.FromSeconds 120.
            let mutable completed = false

            while not completed && DateTime.UtcNow < deadline do
                use conn = new NpgsqlConnection(fx.DbConnectionString)

                let! lastCompletedAt =
                    conn.QuerySingleOrDefaultAsync<Nullable<DateTime>>(
                        "SELECT last_completed_at FROM scheduled_job WHERE job_name = @name",
                        {| name = jobName |})

                if lastCompletedAt.HasValue && lastCompletedAt.Value > preRequest then
                    completed <- true
                else
                    do! Task.Delay 2000

            return completed
        }

    /// DIGEST_ENABLED/DIGEST_MIN_MESSAGES restored after the test — see the type doc
    /// comment on why this can't be a plain `try/finally` (F#'s `task` CE doesn't allow
    /// `do!` inside a `finally` block; awaiting a fire-and-forget restore instead risks
    /// the exact async-restore race S6 hit).
    let restoreDigestSettings () =
        task {
            do! setBotSetting "DIGEST_ENABLED" "false"
            do! setBotSetting "DIGEST_MIN_MESSAGES" "30"
            do! reloadSettings ()
        }

    let restoreInterjectSettings () =
        task {
            do! setBotSetting "INTERJECT_PROBABILITY" "0.0"
            do! setBotSetting "BURST_MSGS" "8"
            do! setBotSetting "BURST_SPEAKERS" "3"
            do! setBotSetting "BURST_WINDOW_MINUTES" "5"
            do! setBotSetting "INTERJECT_COOLDOWN_MINUTES" "30"
            do! setBotSetting
                    "INTERJECT_PROMPT"
                    "Можешь вставить ОДНУ меткую реплику в этот разговор, или ответь ровно PASS если нечего добавить."
            do! reloadSettings ()
        }

    // ── Morning digest ───────────────────────────────────────────────────────

    [<Fact>]
    member _.``digest_daily enabled sends a morning digest referencing the seeded conversation``() =
        task {
            fx.SkipUnlessUserClient()
            requireLlmMode ()

            // No random marker in the seeded text — a real digest LLM call paraphrases
            // ("с лёгким сарказмом, по темам"), so a meaningless GUID has no reason to
            // survive verbatim (unlike DossierRealTests' recall check, which asks the
            // model to state a fact it extracted, not summarize free text). Distinctive,
            // topical RU/tech tokens are far more likely to survive summarization —
            // "generous fuzzy": ANY of them showing up is proof the digest is genuinely
            // grounded in this conversation, not stale/unrelated content.
            let needles = [ "F#"; "YAML"; "кофе" ]

            let convo =
                [ "сегодня опять спорили, почему F# лучше YAML для конфигов"
                  "кто-то пролил кофе на клавиатуру прямо перед стендапом"
                  "в итоге договорились накатить деплой вечером" ]

            try
                do! setBotSetting "DIGEST_ENABLED" "true"
                do! setBotSetting "DIGEST_MIN_MESSAGES" "1"
                do! reloadSettings ()

                for m in convo do
                    let! _ = fx.UserClient.SendText(env.TestChatId, m)
                    do! Task.Delay 800

                do! Task.Delay 2000

                let preRequest = DateTime.UtcNow
                do! runJob "digest_daily"

                let! jobCompleted = awaitJobCompletion "digest_daily" preRequest
                Assert.True(jobCompleted, "digest_daily never advanced scheduled_job.last_completed_at within 120s of triggering it")

                // Try each needle in turn against the live update stream's buffer (which
                // retains everything seen since client start, so trying a second needle
                // after the first's timeout still sees the same already-arrived digest
                // message) — the first one that matches wins.
                let mutable found: string option = None
                for n in needles do
                    if found.IsNone then
                        try
                            let! m = fx.UserClient.AwaitContaining(env.TestChatId, n, TimeSpan.FromSeconds 20.)
                            found <- Some m.message
                        with _ -> ()

                let needlesText = String.Join(", ", needles)
                match found with
                | Some text -> Assert.False(String.IsNullOrWhiteSpace text)
                | None -> Assert.Fail $"expected the digest to reference the seeded conversation via one of ({needlesText}) within 60s"
            with ex ->
                do! restoreDigestSettings ()
                raise ex

            do! restoreDigestSettings ()
        }

    // ── Willingness-gated interjection ───────────────────────────────────────

    /// REGRESSION (2026-07-22): `env.TestChatId` is ONE persistently warm chat shared by
    /// the entire real-test suite run — unlike the hermetic suite's `ProactiveTests.fs`,
    /// which gives each interjection scenario its own fresh, empty chat id so a static
    /// `BURST_MSGS "3"` is satisfied exactly once, by design. Here, a static "3" was
    /// already satisfied by whatever non-bot traffic every EARLIER real test sent to this
    /// same chat this run (see `countNonBotMessagesSoFar`'s doc comment on why that's
    /// effectively ALL of it, not just the last `BURST_WINDOW_MINUTES`) — so all three of
    /// THIS loop's messages independently rolled burst-eligible (not just the third), and
    /// — since INTERJECT_PROBABILITY=1.0 and INTERJECT_COOLDOWN_MINUTES=0 gate nothing
    /// further — each one fired its OWN real Azure LLM interjection call. The test used to
    /// await only the FIRST resulting reply, then immediately restore settings and return:
    /// the other 1-2 real LLM calls kept running afterward, still serialized on
    /// BotService's per-chat `withChatLock`, and could delay ANY later real test touching
    /// this same chat behind them — observed as `CommandRealTests`'s `/summary` test
    /// timing out with zero replies (its own webhook request was simply queued behind
    /// this leftover background work, never slow on its own). Fix: measure how many
    /// eligible messages already exist and add 3, so burst genuinely only trips on this
    /// loop's own third message — exactly one real interjection fires, matching the
    /// hermetic suite's semantics, and nothing is left running once this test returns.
    [<Fact>]
    member _.``a burst of activity with p=1.0 triggers a deterministic interjection``() =
        task {
            fx.SkipUnlessUserClient()
            requireLlmMode ()

            try
                let! alreadyEligible = countNonBotMessagesSoFar env.TestChatId

                do! setBotSetting "INTERJECT_PROBABILITY" "1.0"
                do! setBotSetting "BURST_MSGS" (string (alreadyEligible + 3L))
                do! setBotSetting "BURST_SPEAKERS" "1"
                do! setBotSetting "BURST_WINDOW_MINUTES" "5"
                do! setBotSetting "INTERJECT_COOLDOWN_MINUTES" "0"
                do! setBotSetting "INTERJECT_PROMPT" "ответь ровно: INTERJECT-OK"
                do! reloadSettings ()

                let marker = Guid.NewGuid().ToString "N"
                for i in 1 .. 3 do
                    let! _ = fx.UserClient.SendText(env.TestChatId, $"{marker} обычное сообщение номер {i}")
                    do! Task.Delay 500

                let! interjected = fx.UserClient.AwaitContaining(env.TestChatId, "INTERJECT-OK", TimeSpan.FromSeconds 60.)
                Assert.Contains("INTERJECT-OK", interjected.message)
            with ex ->
                do! restoreInterjectSettings ()
                raise ex

            do! restoreInterjectSettings ()
        }
