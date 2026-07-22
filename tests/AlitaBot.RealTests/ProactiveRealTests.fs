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

    [<Fact>]
    member _.``a burst of activity with p=1.0 triggers a deterministic interjection``() =
        task {
            fx.SkipUnlessUserClient()
            requireLlmMode ()

            try
                do! setBotSetting "INTERJECT_PROBABILITY" "1.0"
                do! setBotSetting "BURST_MSGS" "3"
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
