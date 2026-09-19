namespace Fizruk

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging

/// Ticks GameCore.CheckAllIdle every 10 minutes. Fizruk has no database, so this is
/// a plain hosted service rather than BotInfra.ScheduledJobs (which needs one).
type IdleCheckHostedService(core: GameCore, time: TimeProvider, logger: ILogger<IdleCheckHostedService>) =
    inherit BackgroundService()

    override _.ExecuteAsync(ct: CancellationToken) =
        task {
            use timer = new PeriodicTimer(TimeSpan.FromMinutes 10.0, time)
            while! timer.WaitForNextTickAsync ct do
                if not ct.IsCancellationRequested then
                    try do! core.CheckAllIdle()
                    with ex -> logger.LogError(ex, "Fizruk idle-check tick failed")
        }
        :> Task
