namespace CouponHubBot.Services

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open CouponHubBot
open BotInfra

/// Coupon's job definitions for the generic `BotInfra.ScheduledJobs`/
/// `SchedulerHostedService` lease machinery. Named `CouponScheduledJobs` (not
/// `ScheduledJobs`) to avoid colliding with the opened `BotInfra.ScheduledJobs`
/// module it wraps — same convention as AlitaBot's `AlitaScheduledJobs`.
module CouponScheduledJobs =

    [<Literal>]
    let ReminderJobName = "reminder_daily"

    /// One-shot TEST_MODE override for the reminder job's "now" — set by
    /// `/test/run-reminder`'s optional `?nowUtc=` immediately before triggering
    /// `RunJobNow`, consumed (and cleared) by the very next run of this job.
    /// Deliberately NOT a shared-clock move (no `FakeTimeProvider.AdjustTime`):
    /// that would persist across every later test sharing the same fixture,
    /// silently breaking any test whose "today" assumption (`fixture.
    /// FixedToday`) no longer matches the moved clock. Safe under TEST_MODE's
    /// sequential test execution only — concurrent triggers would race.
    let mutable testNowOverride: DateTime option = None

    let setTestNowOverride (value: DateTime option) = testNowOverride <- value

    /// `REMINDER_HOUR_DUBLIN` is hot-reloadable, so this is recomputed fresh on
    /// every tick/acquire rather than captured once. Dublin's UTC offset (GMT
    /// vs IST) depends on today's date, not a fixed value, so the conversion
    /// goes through the configured hour on TODAY's Dublin date each call.
    let reminderScheduledTimeUtc (time: TimeProvider) (options: IOptions<BotConfiguration>) () : TimeSpan =
        let dublinTz = Utils.TimeZones.getDublinTimeZone()
        let nowDublin = TimeZoneInfo.ConvertTimeFromUtc(time.GetUtcNow().UtcDateTime, dublinTz)
        let hourDublin = options.Value.ReminderHourDublin
        let targetDublin = DateTime(nowDublin.Year, nowDublin.Month, nowDublin.Day, hourDublin, 0, 0)
        (TimeZoneInfo.ConvertTimeToUtc(targetDublin, dublinTz)).TimeOfDay

    /// Builds the job list `BotInfra.SchedulerHostedService` runs for Coupon.
    let jobDefinitions
        (reminder: ReminderService)
        (options: IOptions<BotConfiguration>)
        (time: TimeProvider)
        : ScheduledJobs.JobDefinition list =
        [ { Name = ReminderJobName
            ScheduledTimeUtc = reminderScheduledTimeUtc time options
            Run = fun () ->
                task {
                    let nowUtc = testNowOverride
                    testNowOverride <- None
                    do! reminder.RunOnce(?nowUtc = nowUtc) :> Task
                } :> Task } ]

/// `REMINDER_RUN_ON_START` catch-up: on pod startup, tries the SAME
/// `reminder_daily` lease the scheduled tick uses (via the low-level
/// `ScheduledJobs.tryAcquire`/`complete` functions, not the tick loop) so that
/// several pods restarting together (rolling deploy) still run the reminder at
/// most once, not once per pod. A no-op unless today's scheduled Dublin hour
/// has already passed and no pod has completed it yet — this is a same-day
/// catch-up, not an unconditional startup fire.
type ReminderRunOnStartService(
    connString: string,
    time: TimeProvider,
    options: IOptions<BotConfiguration>,
    reminder: ReminderService,
    logger: ILogger<ReminderRunOnStartService>
) =
    interface IHostedService with
        member _.StartAsync(_ct: CancellationToken) =
            task {
                if options.Value.ReminderRunOnStart then
                    let scheduledTime = CouponScheduledJobs.reminderScheduledTimeUtc time options ()
                    let! acquired =
                        ScheduledJobs.tryAcquire
                            connString
                            time
                            CouponScheduledJobs.ReminderJobName
                            scheduledTime
                            Environment.MachineName
                    if acquired then
                        try
                            let! _ = reminder.RunOnce()
                            do! ScheduledJobs.complete connString time CouponScheduledJobs.ReminderJobName
                            logger.LogInformation("ReminderRunOnStart: ran reminder_daily")
                        with ex ->
                            logger.LogError(ex, "ReminderRunOnStart: reminder_daily failed")
                    else
                        logger.LogInformation("ReminderRunOnStart: reminder_daily not due or already completed today")
            } :> Task

        member _.StopAsync(_ct: CancellationToken) = Task.CompletedTask
