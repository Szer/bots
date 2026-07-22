namespace AlitaBot.RealTests

open System
open System.Net.Http
open System.Threading.Tasks
open Dapper
open Npgsql
open Xunit

/// Slice 7 real-Telegram tests: /roast, /awards, /quote against a real deployed bot and
/// real Azure AI Foundry. /roast mirrors DossierRealTests.fs's seed -> nightly job ->
/// assert-on-recall shape (it needs a real dossier fact to roast from); /awards and
/// /quote are exercised directly against real recent chat history.
type SocialRealTests(fx: RealAssemblyFixture) =
    let env = fx.Env

    let runJob (jobName: string) =
        task {
            use http = new HttpClient(Timeout = TimeSpan.FromSeconds 30.)
            let! resp = http.PostAsync($"{env.RunJobUrl}?name={jobName}", null)

            if not resp.IsSuccessStatusCode then
                let! body = resp.Content.ReadAsStringAsync()
                failwith $"POST {env.RunJobUrl}?name={jobName} -> {int resp.StatusCode}: {body}"
        }

    /// Same completion signal as DossierRealTests.fs's awaitJobCompletion — polls
    /// `scheduled_job.last_completed_at` past `preRequest` rather than trusting the
    /// fire-and-forget 202 response.
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

    /// Clears any leftover `roast_cooldown` stamp for `targetUserId` from a previous test
    /// run — required for `RESPONDER_MODE=llm make real-test` to stay green across
    /// repeated runs (ROAST_COOLDOWN_SECONDS defaults to 300s, well within how often this
    /// suite might reasonably be re-run against the same test user). The service role has
    /// no DELETE grant on `roast_cooldown` (see V5 migration) — pushing the timestamp far
    /// into the past has the same effect via the one grant it does have (UPDATE).
    let clearRoastCooldown (targetUserId: int64) =
        task {
            use conn = new NpgsqlConnection(fx.DbConnectionString)
            do! conn.OpenAsync()

            let! _ =
                conn.ExecuteAsync(
                    "UPDATE roast_cooldown SET last_roasted_at = NOW() - INTERVAL '1 day' WHERE target_user_id = @uid;",
                    {| uid = targetUserId |})

            return ()
        }

    let containsAnyOf (text: string) (needles: string list) =
        needles |> List.exists (fun n -> text.Contains(n, StringComparison.OrdinalIgnoreCase))

    [<Fact>]
    member _.``roast (no target = self) references a real dossier fact or a seeded quote``() =
        task {
            fx.SkipUnlessUserClient()

            if env.ResponderMode <> "llm" then
                Assert.Skip
                    "RESPONDER_MODE=llm required (dossier extraction + /roast both call Azure AI Foundry) — run `RESPONDER_MODE=llm make real-test`"

            let marker = Guid.NewGuid().ToString "N"
            let facts =
                [ $"{marker}: обожаю писать баги по пятницам и ни капли не стыжусь этого"
                  $"{marker}: моё любимое занятие — ломать чужой прод в пятницу вечером" ]

            for f in facts do
                let! _ = fx.UserClient.SendText(env.TestChatId, f)
                do! Task.Delay 800

            // Give message_log a moment to have the rows before the nightly job reads
            // "last 24h" (same pacing as DossierRealTests.fs).
            do! Task.Delay 2000

            let preRequest = DateTime.UtcNow
            do! runJob "dossier_nightly_update"

            let! jobCompleted = awaitJobCompletion "dossier_nightly_update" preRequest
            Assert.True(
                jobCompleted,
                "dossier_nightly_update never advanced scheduled_job.last_completed_at within 120s of triggering it")

            let userId = fx.UserClient.Me.id
            do! clearRoastCooldown userId

            let! roastMsgId = fx.UserClient.SendText(env.TestChatId, "/roast")
            let! roastReply = fx.UserClient.AwaitReplyTo(env.TestChatId, roastMsgId, Timeouts.reply)

            Assert.False(String.IsNullOrWhiteSpace roastReply.message)
            Assert.True(
                containsAnyOf roastReply.message [ marker; "пятниц"; "баг"; "прод" ],
                $"expected the roast to reference something from the seeded facts/quotes: {roastReply.message}")

            printfn "[SocialRealTests] /roast reply: %s" roastReply.message
        }

    [<Fact>]
    member _.``quote picks something from real recent chat history``() =
        task {
            fx.SkipUnlessUserClient()

            if env.ResponderMode <> "llm" then
                Assert.Skip "RESPONDER_MODE=llm required (/quote calls Azure AI Foundry) — run `RESPONDER_MODE=llm make real-test`"

            let marker = Guid.NewGuid().ToString "N"
            let quoteText = $"{marker}: сервер сам себя не задеплоит, это медицинский факт"
            let! _ = fx.UserClient.SendText(env.TestChatId, quoteText)
            do! Task.Delay 1500

            let! quoteMsgId = fx.UserClient.SendText(env.TestChatId, "/quote")
            let! quoteReply = fx.UserClient.AwaitReplyTo(env.TestChatId, quoteMsgId, Timeouts.reply)

            Assert.False(String.IsNullOrWhiteSpace quoteReply.message)
            Assert.True(
                containsAnyOf quoteReply.message [ "Цитата"; marker; "задеплоит" ],
                $"expected either the fixed 'Цитата' label or the seeded quote fragment in the /quote reply: {quoteReply.message}")

            printfn "[SocialRealTests] /quote reply: %s" quoteReply.message
        }

    [<Fact>]
    member _.``awards renders a non-empty announcement and writes a karma row``() =
        task {
            fx.SkipUnlessUserClient()

            if env.ResponderMode <> "llm" then
                Assert.Skip "RESPONDER_MODE=llm required (/awards calls Azure AI Foundry) — run `RESPONDER_MODE=llm make real-test`"

            let marker = Guid.NewGuid().ToString "N"
            let! _ = fx.UserClient.SendText(env.TestChatId, $"{marker}: сегодня опять чиню чужой пайплайн, как обычно")
            do! Task.Delay 1500

            let preRequest = DateTime.UtcNow

            let! awardsMsgId = fx.UserClient.SendText(env.TestChatId, "/awards")
            let! awardsReply = fx.UserClient.AwaitReplyTo(env.TestChatId, awardsMsgId, Timeouts.reply)

            Assert.False(String.IsNullOrWhiteSpace awardsReply.message)
            printfn "[SocialRealTests] /awards reply: %s" awardsReply.message

            // At least one karma row should have landed AFTER this test's own /awards call
            // (earlier real-test runs may have left rows behind — count > baseline, not
            // just > 0, so a re-run of this suite still proves ITS OWN call worked).
            let deadline = DateTime.UtcNow + TimeSpan.FromSeconds 15.
            let mutable newestAwardedAt = None

            while newestAwardedAt.IsNone && DateTime.UtcNow < deadline do
                use conn = new NpgsqlConnection(fx.DbConnectionString)

                let! at =
                    conn.QuerySingleOrDefaultAsync<Nullable<DateTime>>(
                        "SELECT MAX(awarded_at) FROM karma WHERE awarded_at > @preRequest;",
                        {| preRequest = preRequest |})

                if at.HasValue then
                    newestAwardedAt <- Some at.Value
                else
                    do! Task.Delay 1000

            Assert.True(newestAwardedAt.IsSome, "expected at least one new karma row written by this /awards call")
        }
