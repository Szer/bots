namespace AlitaBot.Services

open System
open System.Threading.Tasks
open Microsoft.Extensions.Options
open AlitaBot
open BotInfra

/// Alita's job definitions for the generic scheduling machinery in
/// `BotInfra.ScheduledJobs`/`BotInfra.SchedulerHostedService` (promoted there from this
/// file — see `git log -- src/AlitaBot/Services/ScheduledJobs.fs` for the pre-promotion
/// history of the lease/loop machinery itself). Drives the nightly per-person dossier fact
/// extraction (Slice 5b) and the daily morning digest (Slice 8). Program.fs constructs a
/// `BotInfra.SchedulerHostedService` with `jobDefinitions` below and registers it as a
/// hosted service; the same service also backs the TEST_MODE `/test/run-job?name=` hook.
/// Job names here are matched by string literal from DossierTests.fs/SocialTests.fs/
/// ProactiveTests.fs (and the `*RealTests.fs` counterparts) — keep them in sync.
/// Named `AlitaScheduledJobs` (not `ScheduledJobs`) to avoid colliding with the opened
/// `BotInfra.ScheduledJobs` module it wraps.
module AlitaScheduledJobs =

    [<Literal>]
    let DossierJobName = "dossier_nightly_update"

    [<Literal>]
    let DossierScheduledHourUtc = 2

    [<Literal>]
    let DigestJobName = "digest_daily"

    /// Builds the job list `BotInfra.SchedulerHostedService` should run for Alita.
    /// `DIGEST_UTC_HOUR` is a hot-reloadable `bot_setting` (unlike the dossier job's
    /// compile-time-fixed hour) — read fresh from `options.Value` inside `ScheduledTimeUtc`
    /// so a live change takes effect on the very next tick, no restart needed.
    let jobDefinitions
        (dossier: DossierService)
        (digest: DigestService)
        (options: IOptions<BotConfiguration>)
        : ScheduledJobs.JobDefinition list =
        [ { Name = DossierJobName
            ScheduledTimeUtc = fun () -> TimeSpan.FromHours(float DossierScheduledHourUtc)
            Run = fun () -> dossier.RunNightlyUpdate() :> Task }
          { Name = DigestJobName
            ScheduledTimeUtc = fun () -> TimeSpan.FromHours(float options.Value.DigestUtcHour)
            Run = fun () -> digest.RunDailyDigest() :> Task } ]
