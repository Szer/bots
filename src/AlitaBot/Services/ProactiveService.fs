namespace AlitaBot.Services

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Telegram.Bot
open AlitaBot

type ProactiveService(
    botClient: ITelegramBotClient,
    pipeline:  LlmPipeline,
    db:        DbService,
    botConf:   BotConfiguration,
    logger:    ILogger<ProactiveService>,
    time:      TimeProvider
) =
    inherit BackgroundService()

    let rng = Random.Shared

    let isWithinActiveHours () =
        let hour = time.GetUtcNow().Hour
        hour >= botConf.ProactiveActiveHoursStart && hour < botConf.ProactiveActiveHoursEnd

    override _.ExecuteAsync(ct: CancellationToken) = (task {
        // Check every hour
        use timer = new PeriodicTimer(TimeSpan.FromHours 1.0)
        while! timer.WaitForNextTickAsync(ct) do
            if not ct.IsCancellationRequested then
                try
                    if not (isWithinActiveHours()) then ()
                    elif rng.NextDouble() > botConf.ProactivePostProbability then ()
                    else
                        // Pick oldest unposted news or generate a spontaneous thought
                        let! newsOpt = db.GetOldestUnpostedNews()
                        let! replyOpt =
                            match newsOpt with
                            | Some news ->
                                pipeline.GenerateProactive(
                                    $"Share this news with the group in your own words: {news.Summary}")
                            | None ->
                                pipeline.GenerateProactive(
                                    "Поделись случайной мыслью, воспоминанием или наблюдением с группой.")

                        match replyOpt with
                        | None -> ()
                        | Some text ->
                            do! botClient.SendMessage(Telegram.Bot.Types.ChatId(botConf.TargetChatId), text) :> Task
                            match newsOpt with
                            | Some news -> do! db.MarkNewsPosted(news.Id)
                            | None      -> ()
                            Metrics.proactivePostsTotal.Add(1L)
                            logger.LogInformation("ProactiveService: posted to chat {ChatId}", botConf.TargetChatId)
                with ex ->
                    logger.LogError(ex, "ProactiveService: error during proactive tick")
    } :> Task)
