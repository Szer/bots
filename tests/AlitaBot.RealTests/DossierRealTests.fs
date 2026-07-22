namespace AlitaBot.RealTests

open System
open System.Net.Http
open System.Threading.Tasks
open Dapper
open Npgsql
open Xunit

/// Phase-1 Slice 5b real-Telegram test: seeds a few personal-fact messages from the test
/// user, triggers the nightly dossier job via the TEST_MODE-only `/test/run-job` endpoint
/// (`RealEnv.RunJobUrl` — reachable because `DevDb.applyRealSettingsAsync` now forces
/// TEST_MODE=true on every real-test run, see ScheduledJobs.fs's
/// `SchedulerHostedService.RunJobNow`), polls `person_dossier` for a row, then mentions
/// the bot asking what it knows about the sender and asserts the reply references a
/// seeded fact — exercising the full extract -> embed -> dedup -> insert -> merge -> recall
/// round trip against real Azure AI Foundry (not the fake suite's deterministic
/// hash-of-text vectors, see `tests/AlitaBot.Tests/DossierTests.fs`).
///
/// `/test/run-job` is fire-and-forget on the bot side (`RunJobNow`'s doc comment): the 202
/// response lands as soon as the job is kicked off, not once it's finished. Awaiting it
/// inline used to hold the request open for the job's full duration (two sequential real
/// Azure AI Foundry calls per active user), which exceeded the CI real-test AKS gateway's
/// ~15s upstream timeout (`504: upstream request timeout`) — this is the hotfix that CI
/// failure prompted. `awaitJobCompletion` below polls `scheduled_job.last_completed_at`
/// (stamped by `ScheduledJobs.completeTestTriggered`, using the real wall clock rather than
/// the pod's TEST_MODE-frozen `TimeProvider`) instead of trusting the HTTP response.
type DossierRealTests(fx: RealAssemblyFixture) =
    let env = fx.Env

    let runJob (jobName: string) =
        task {
            use http = new HttpClient(Timeout = TimeSpan.FromSeconds 30.)
            let! resp = http.PostAsync($"{env.RunJobUrl}?name={jobName}", null)

            if not resp.IsSuccessStatusCode then
                let! body = resp.Content.ReadAsStringAsync()
                failwith $"POST {env.RunJobUrl}?name={jobName} -> {int resp.StatusCode}: {body}"
        }

    /// Polls `scheduled_job.last_completed_at` until it advances past `preRequest` (the
    /// wall-clock time sampled right before the `POST /test/run-job`), proving the job's
    /// full extract -> embed -> dedup -> insert -> merge round trip actually finished on the
    /// bot side — not just that the fire-and-forget POST was accepted. Generous timeout:
    /// two sequential real Azure AI Foundry calls per active user, against real latency.
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

    /// Polls for a `person_dossier` row for `userId`. Called after `awaitJobCompletion`
    /// has already confirmed the job finished, so this is a short confirmation read (the
    /// row, if any, was committed before `last_completed_at` advanced) rather than the
    /// primary completion signal.
    let awaitDossier (userId: int64) =
        task {
            let deadline = DateTime.UtcNow + TimeSpan.FromSeconds 15.
            let mutable found = None

            while found.IsNone && DateTime.UtcNow < deadline do
                use conn = new NpgsqlConnection(fx.DbConnectionString)

                let! row =
                    conn.QuerySingleOrDefaultAsync<{| summary: string |}>(
                        "SELECT summary FROM person_dossier WHERE user_id = @uid",
                        {| uid = userId |})

                if box row <> null then
                    found <- Some row.summary
                else
                    do! Task.Delay 1500

            return found
        }

    let containsAnyOf (text: string) (needles: string list) =
        needles |> List.exists (fun n -> text.Contains(n, StringComparison.OrdinalIgnoreCase))

    [<Fact>]
    member _.``nightly job extracts facts from real Telegram messages and the bot recalls one when asked``() =
        task {
            fx.SkipUnlessUserClient()

            if env.ResponderMode <> "llm" then
                Assert.Skip
                    "RESPONDER_MODE=llm required (dossier extraction + recall both call Azure AI Foundry) — run `RESPONDER_MODE=llm make real-test`"

            let marker = Guid.NewGuid().ToString "N"
            let needles = [ "F#"; "YAML"; "кофе" ]

            let facts =
                [ $"{marker}: я обожаю писать на F# и ненавижу YAML"
                  $"{marker}: ещё обожаю пить кофе по утрам перед тем как писать код"
                  $"{marker}: выходные обычно провожу за настольными играми" ]

            for f in facts do
                let! _ = fx.UserClient.SendText(env.TestChatId, f)
                do! Task.Delay 800

            // Give message_log a moment to have the rows before the job reads "last 24h".
            do! Task.Delay 2000

            let preRequest = DateTime.UtcNow
            do! runJob "dossier_nightly_update"

            let! jobCompleted = awaitJobCompletion "dossier_nightly_update" preRequest
            Assert.True(
                jobCompleted,
                "dossier_nightly_update never advanced scheduled_job.last_completed_at within 120s of triggering it")

            let userId = fx.UserClient.Me.id

            match! awaitDossier userId with
            | None -> Assert.Fail $"no person_dossier row for user {userId} within 15s of the nightly job completing"
            | Some summary ->
                Assert.False(String.IsNullOrWhiteSpace summary)

                let needlesText = String.Join(", ", needles)

                let! askMsgId = fx.UserClient.SendText(env.TestChatId, $"@{env.BotUsername} что ты обо мне знаешь?")

                // The recall-injected reply goes through the normal triggered-message path
                // (ResponderService "llm" mode -> CompleteStream -> EditThrottleRenderer),
                // which sends a short first chunk and then edits it into its final form —
                // AwaitReplyTo alone would catch that first, still-streaming chunk (a
                // partial sentence), not the finished text. Settle on edits first, same as
                // SmokeTests.fs's "streamed reply settles into a final text".
                let! reply = fx.UserClient.AwaitReplyTo(env.TestChatId, askMsgId, Timeouts.reply)
                let! finalText = fx.UserClient.AwaitEditsSettled(env.TestChatId, reply.id, Timeouts.editQuiet)

                Assert.False(String.IsNullOrWhiteSpace finalText)
                Assert.True(
                    containsAnyOf finalText needles,
                    $"expected the reply to reference a known fact about the sender ({needlesText}): {finalText}")

                let! dossierMsgId = fx.UserClient.SendText(env.TestChatId, "/dossier")
                let! dossierReply = fx.UserClient.AwaitReplyTo(env.TestChatId, dossierMsgId, Timeouts.reply)

                Assert.True(
                    containsAnyOf dossierReply.message needles,
                    $"expected /dossier to show a known fact ({needlesText}): {dossierReply.message}")
        }
