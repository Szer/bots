namespace AlitaBot.Services

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open AlitaBot

type SchedulerService(
    db:      DbService,
    dossier: DossierService,
    news:    NewsService,
    botConf: BotConfiguration,
    logger:  ILogger<SchedulerService>,
    time:    TimeProvider
) =
    inherit BackgroundService()

    let podId = System.Net.Dns.GetHostName()

    let tryRunDailyJob (name: string) (scheduledHourUtc: int) (action: unit -> Task) = task {
        let scheduledTime = TimeSpan.FromHours(float scheduledHourUtc)
        let! acquired = db.TryAcquireJob(name, scheduledTime, podId)
        if acquired then
            logger.LogInformation("SchedulerService: acquired job {Job}", name)
            try
                do! action()
                do! db.CompleteJob(name)
                logger.LogInformation("SchedulerService: completed job {Job}", name)
            with ex ->
                logger.LogError(ex, "SchedulerService: job {Job} failed", name)
    }

    let tryRunIntervalJob (name: string) (intervalHours: int) (action: unit -> Task) = task {
        let interval = TimeSpan.FromHours(float intervalHours)
        let! acquired = db.TryAcquireIntervalJob(name, interval, podId)
        if acquired then
            logger.LogInformation("SchedulerService: acquired interval job {Job}", name)
            try
                do! action()
                do! db.CompleteJob(name)
                logger.LogInformation("SchedulerService: completed interval job {Job}", name)
            with ex ->
                logger.LogError(ex, "SchedulerService: interval job {Job} failed", name)
    }

    override _.ExecuteAsync(ct: CancellationToken) = (task {
        // Check every 10 minutes
        use timer = new PeriodicTimer(TimeSpan.FromMinutes 10.0)
        while! timer.WaitForNextTickAsync(ct) do
            if not ct.IsCancellationRequested then
                try
                    // Daily dossier update at configured hour (default 02:00 UTC)
                    do! tryRunDailyJob "daily_dossier_update" botConf.DossierUpdateHourUtc
                            (fun () -> dossier.RunDailyUpdate())

                    // News fetch on interval (default every 4h)
                    do! tryRunIntervalJob "daily_news_fetch" botConf.NewsFetchIntervalHours
                            (fun () -> task { let! _ = news.FetchAndSummarise() in return () })

                    // Cleanup at 03:00 UTC
                    do! tryRunDailyJob "daily_cleanup" 3
                            (fun () -> db.PurgeOldMessages(botConf.MessageLogRetentionDays))
                with ex ->
                    logger.LogError(ex, "SchedulerService: error during tick")
    } :> Task)

    /// Test-mode hook: run a named job immediately regardless of schedule.
    member this.RunJobNow(jobName: string) : Task = task {
        match jobName with
        | "daily_dossier_update" -> do! dossier.RunDailyUpdate()
        | "daily_news_fetch"     -> let! _ = news.FetchAndSummarise() in ()
        | "daily_cleanup"        -> do! db.PurgeOldMessages(botConf.MessageLogRetentionDays)
        | other                  -> logger.LogWarning("SchedulerService: unknown job {Job}", other)
    }
