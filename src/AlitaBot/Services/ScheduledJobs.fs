namespace AlitaBot.Services

open System
open System.Threading
open System.Threading.Tasks
open Dapper
open Npgsql
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open AlitaBot.Telemetry

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

/// Hosted background service driving the nightly dossier job, plus a TEST_MODE hook
/// (`RunJobNow`, wired to `/test/run-job?name=` in Program.fs) that runs it immediately,
/// bypassing the lease/schedule check entirely — mirrors the old `SchedulerService`'s
/// `RunJobNow`. Ticks every 10 minutes; `tryAcquire`'s own `scheduledTime` check is what
/// actually gates whether today's run has become due, not the tick interval.
type SchedulerHostedService
    (
        connString: string,
        time: TimeProvider,
        dossier: DossierService,
        logger: ILogger<SchedulerHostedService>
    ) =
    inherit BackgroundService()

    [<Literal>]
    let DossierJobName = "dossier_nightly_update"

    [<Literal>]
    let DossierScheduledHourUtc = 2

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

    override _.ExecuteAsync(ct: CancellationToken) =
        task {
            use timer = new PeriodicTimer(TimeSpan.FromMinutes 10.0, time)
            while! timer.WaitForNextTickAsync(ct) do
                if not ct.IsCancellationRequested then
                    try
                        do! runDossierJob ()
                    with ex ->
                        logger.LogError(ex, "ScheduledJobs: error during tick")
        }
        :> Task

    /// TEST_MODE hook (`/test/run-job?name=`): runs a named job immediately, regardless of
    /// the lease/schedule — never touches `scheduled_job`'s lease columns at all, so it
    /// can't collide with (or advance) the real nightly schedule.
    member _.RunJobNow(jobName: string) : Task =
        task {
            match jobName with
            | n when n = DossierJobName -> do! dossier.RunNightlyUpdate ()
            | other -> logger.LogWarning("ScheduledJobs: unknown job {Job}", other)
        }
