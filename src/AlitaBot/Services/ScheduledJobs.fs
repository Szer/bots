namespace AlitaBot.Services

open System
open System.Threading
open System.Threading.Tasks
open Dapper
open Npgsql
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open AlitaBot
open AlitaBot.Telemetry
open BotInfra

/// Distributed scheduled-job locking (Slice 5b) — local adaptation of the old (pre-
/// Funogram) `feature/alita-bot` branch's `src/BotInfra/ScheduledJobs.fs` (see
/// `git show origin/feature/alita-bot:src/BotInfra/ScheduledJobs.fs`), copied verbatim in
/// lease semantics (the `UPDATE ... RETURNING` atomic-acquire pattern) but living inside
/// `AlitaBot.Services` rather than `BotInfra` — a `BotInfra` change would trigger prod
/// redeploys of VahterBanBot/CouponHubBot too, and nothing outside AlitaBot needs a
/// scheduler yet. Promoting this to `BotInfra` (if/when a second bot needs one) is a
/// deliberate, reviewed, daytime PR — not something to fold in here. See
/// `docs/TECH-DEBT.md`.
module ScheduledJobs =

    /// Tries to acquire a scheduled job's lease for the given pod. Returns true if this
    /// pod successfully locked the job (it's due — `now` has passed today's
    /// `scheduledTime` — and no other pod currently holds the lock).
    let tryAcquire
        (connString: string)
        (timeProvider: TimeProvider)
        (jobName: string)
        (scheduledTime: TimeSpan)
        (podId: string)
        : Task<bool> =
        task {
            use conn = new NpgsqlConnection(connString)
            let now = timeProvider.GetUtcNow().UtcDateTime
            //language=postgresql
            let sql =
                """
UPDATE scheduled_job
SET locked_until = @now + INTERVAL '1 hour',
    locked_by    = @podId
WHERE job_name = @jobName
  AND @now >= (CURRENT_DATE + @scheduledTime)
  AND (last_completed_at IS NULL OR last_completed_at < (CURRENT_DATE + @scheduledTime))
  AND (locked_until IS NULL OR locked_until < @now)
RETURNING job_name;
"""
            let! result =
                conn.QueryAsync<string>(
                    sql,
                    {| jobName = jobName
                       scheduledTime = scheduledTime
                       podId = podId
                       now = now |})
            return Seq.length result > 0
        }

    /// Releases the lease and records completion time for a scheduled job.
    let complete (connString: string) (timeProvider: TimeProvider) (jobName: string) : Task =
        task {
            use conn = new NpgsqlConnection(connString)
            let now = timeProvider.GetUtcNow().UtcDateTime
            //language=postgresql
            let sql =
                """
UPDATE scheduled_job
SET last_completed_at = @now,
    locked_until      = NULL,
    locked_by         = NULL
WHERE job_name = @jobName;
"""
            let! _ = conn.ExecuteAsync(sql, {| jobName = jobName; now = now |})
            return ()
        }

    /// Records completion for a TEST_MODE-triggered run (`RunJobNow`), using the real
    /// system clock (`DateTime.UtcNow`) rather than the injected `TimeProvider` — Program.fs
    /// overwrites the DI `TimeProvider` with a **frozen** `FakeTimeProvider` whenever
    /// TEST_MODE is on (see `Program.fs`'s TestMode block), and that clock is never advanced
    /// across a whole fake-suite assembly run or a real-test pod's lifetime (see
    /// `ContainerTestBase.fs`'s `TruncateMemoryTables` doc comment). Writing `complete`'s
    /// `timeProvider.GetUtcNow()` there would make every TEST_MODE-triggered completion land
    /// on the exact same timestamp, so a caller polling for "`last_completed_at` advanced
    /// past a pre-request snapshot" could hang forever on any run after the first one. Only
    /// `last_completed_at` is touched — `locked_until`/`locked_by` are left alone because
    /// `RunJobNow` never goes through `tryAcquire` in the first place, so there's no lease to
    /// release.
    let completeTestTriggered (connString: string) (jobName: string) : Task =
        task {
            use conn = new NpgsqlConnection(connString)
            let now = DateTime.UtcNow
            //language=postgresql
            let sql =
                """
UPDATE scheduled_job
SET last_completed_at = @now
WHERE job_name = @jobName;
"""
            let! _ = conn.ExecuteAsync(sql, {| jobName = jobName; now = now |})
            return ()
        }

/// Hosted background service driving the nightly dossier job and (Slice 8) the daily
/// morning digest job, plus a TEST_MODE hook (`RunJobNow`, wired to `/test/run-job?name=`
/// in Program.fs) that runs either immediately, bypassing the lease/schedule check
/// entirely — mirrors the old `SchedulerService`'s `RunJobNow`. Ticks every 10 minutes;
/// `tryAcquire`'s own `scheduledTime` check is what actually gates whether today's run has
/// become due, not the tick interval.
type SchedulerHostedService
    (
        connString: string,
        time: TimeProvider,
        dossier: DossierService,
        digest: DigestService,
        options: IOptions<BotConfiguration>,
        logger: ILogger<SchedulerHostedService>
    ) =
    inherit BackgroundService()

    [<Literal>]
    let DossierJobName = "dossier_nightly_update"

    [<Literal>]
    let DossierScheduledHourUtc = 2

    [<Literal>]
    let DigestJobName = "digest_daily"

    let podId = Environment.MachineName

    let runDossierJob () =
        task {
            let! acquired =
                ScheduledJobs.tryAcquire connString time DossierJobName (TimeSpan.FromHours(float DossierScheduledHourUtc)) podId
            if acquired then
                logger.LogInformation("ScheduledJobs: acquired {Job}", DossierJobName)
                try
                    do! dossier.RunNightlyUpdate()
                    do! ScheduledJobs.complete connString time DossierJobName
                    logger.LogInformation("ScheduledJobs: completed {Job}", DossierJobName)
                with ex ->
                    logger.LogError(ex, "ScheduledJobs: {Job} failed", DossierJobName)
        }

    /// `DIGEST_UTC_HOUR` is a hot-reloadable `bot_setting` (unlike the dossier job's
    /// compile-time-fixed hour) — read fresh from `options.Value` on every tick so a
    /// live change takes effect on the very next tick, no restart needed.
    let runDigestJob () =
        task {
            let scheduledHour = options.Value.DigestUtcHour
            let! acquired =
                ScheduledJobs.tryAcquire connString time DigestJobName (TimeSpan.FromHours(float scheduledHour)) podId
            if acquired then
                logger.LogInformation("ScheduledJobs: acquired {Job}", DigestJobName)
                try
                    do! digest.RunDailyDigest()
                    do! ScheduledJobs.complete connString time DigestJobName
                    logger.LogInformation("ScheduledJobs: completed {Job}", DigestJobName)
                with ex ->
                    logger.LogError(ex, "ScheduledJobs: {Job} failed", DigestJobName)
        }

    override _.ExecuteAsync(ct: CancellationToken) =
        task {
            use timer = new PeriodicTimer(TimeSpan.FromMinutes 10.0, time)
            while! timer.WaitForNextTickAsync(ct) do
                if not ct.IsCancellationRequested then
                    try
                        do! runDossierJob ()
                    with ex ->
                        logger.LogError(ex, "ScheduledJobs: error during tick")
                    try
                        do! runDigestJob ()
                    with ex ->
                        logger.LogError(ex, "ScheduledJobs: error during tick")
        }
        :> Task

    /// TEST_MODE hook (`/test/run-job?name=`): starts a named job immediately, regardless
    /// of the lease/schedule (never acquires the lease or touches `locked_until`/
    /// `locked_by`, so it can't collide with a real pod's in-progress run), and returns as
    /// soon as it's kicked off rather than waiting for it to finish (`BotInfra.Utils.
    /// fireAndForget`). The job itself can run for tens of seconds (two sequential real
    /// LLM calls against Azure AI Foundry) — awaiting it inline here would hold the HTTP
    /// response open for that whole time, which exceeded the CI real-test AKS gateway's
    /// request timeout ("504: upstream request timeout", DossierRealTests). Once the
    /// fire-and-forget work finishes, it does stamp `last_completed_at` (via
    /// `completeTestTriggered`) so a caller has something to poll for — see that function's
    /// doc comment for why it can't just reuse `complete`. Callers otherwise poll the
    /// database for the job's effects (both DossierTests.fs and DossierRealTests.fs do —
    /// the nightly job's own result was always only observable that way, via
    /// `person_dossier`/`interaction_memory`, never via the HTTP response body).
    member _.RunJobNow(jobName: string) : Task =
        task {
            match jobName with
            | n when n = DossierJobName ->
                fireAndForget logger "scheduled_jobs.run_now" (fun () ->
                    task {
                        do! dossier.RunNightlyUpdate ()
                        do! ScheduledJobs.completeTestTriggered connString DossierJobName
                    }
                    :> Task)
            | n when n = DigestJobName ->
                fireAndForget logger "scheduled_jobs.run_now" (fun () ->
                    task {
                        do! digest.RunDailyDigest ()
                        do! ScheduledJobs.completeTestTriggered connString DigestJobName
                    }
                    :> Task)
            | other -> logger.LogWarning("ScheduledJobs: unknown job {Job}", other)
        }

    /// Whether `jobName` is one `RunJobNow` recognizes — `/test/run-job` uses this to 404
    /// unknown names up front rather than accepting the request and then just logging a
    /// warning from inside the fire-and-forget task.
    member _.IsKnownJob(jobName: string) : bool = jobName = DossierJobName || jobName = DigestJobName
