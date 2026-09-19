namespace Fizruk

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open BotInfra

/// Renders GameCore's user-facing text. Kept separate from the k8s/network calls
/// above so the wording can be read and tested at a glance.
module private StatusText =

    let formatNodeLine (node: NodeInfo option) (now: DateTimeOffset) =
        match node with
        | None -> "Node: unknown."
        | Some n ->
            let readyText = if n.Ready then "Ready" else "NotReady"
            let ageText =
                match n.CreationTimestamp with
                | Some ts -> $"{int (now - ts).TotalMinutes}m"
                | None -> "unknown"
            $"Node: {n.Name} ({readyText}, age {ageText})"

    let formatPlayersLine (players: PlayersProbeResult) =
        match players with
        | PlayersProbeResult.Ok(PlayersInfo.Names []) -> "Players: nobody online."
        | PlayersProbeResult.Ok(PlayersInfo.Names names) -> $"""Players: {String.Join(", ", names)}."""
        | PlayersProbeResult.Ok(PlayersInfo.Count 0) -> "Players: nobody online."
        | PlayersProbeResult.Ok(PlayersInfo.Count n) -> $"Players: {n} online."
        | PlayersProbeResult.Error msg -> $"Players: unknown ({msg})."
        | PlayersProbeResult.NotConfigured -> "Players: unknown."

    let formatStatus (game: GameConfig) (desired: int) (pod: PodInfo option) (node: NodeInfo option) (players: PlayersProbeResult) (now: DateTimeOffset) : string =
        let prefix = game.DisplayName
        if desired = 0 then
            $"{prefix}: stopped."
        else
            match pod with
            | None -> $"{prefix}: starting (pod pending)."
            | Some p when not p.Ready -> $"{prefix}: starting (pod {p.Phase})."
            | Some _ ->
                [ $"{prefix}: running."
                  formatNodeLine node now
                  formatPlayersLine players
                  $"Address: {game.Address}" ]
                |> String.concat "\n"

    /// "who was online at shutdown" summary for the /stop reply.
    let formatPlayersAtShutdown (players: PlayersProbeResult) : string =
        match players with
        | PlayersProbeResult.Ok(PlayersInfo.Names []) -> "nobody"
        | PlayersProbeResult.Ok(PlayersInfo.Names names) -> String.Join(", ", names)
        | PlayersProbeResult.Ok(PlayersInfo.Count 0) -> "nobody"
        | PlayersProbeResult.Ok(PlayersInfo.Count n) -> string n
        | PlayersProbeResult.Error _
        | PlayersProbeResult.NotConfigured -> "unknown"

    let formatUpFor (ageMinutes: int option) : string =
        match ageMinutes with
        | Some m -> $"{m / 60}h {m % 60}m"
        | None -> "unknown"

/// One game's runtime behaviour: state reads, start/stop, the start watcher, and the
/// idle check, serialised per game so a double /start or /stop is a safe no-op.
type GameCore(config: FizrukConfig, k8s: IK8sGateway, notifier: INotifier, time: TimeProvider, pollInterval: TimeSpan, logger: ILogger<GameCore>) =

    let locks = ConcurrentDictionary<string, SemaphoreSlim>()
    let watching = ConcurrentDictionary<string, bool>()

    let lockFor gameId = locks.GetOrAdd(gameId, fun _ -> new SemaphoreSlim(1, 1))

    let gameConfig (gameId: string) = config.Games.[gameId]

    let chatsFor (gameId: string) =
        config.Chats
        |> Map.toList
        |> List.filter (fun (_, games) -> List.contains gameId games)
        |> List.map fst

    let notify (gameId: string) (text: string) =
        task {
            match chatsFor gameId with
            | [] -> ()
            | chats -> do! notifier.NotifyChats(chats, text)
        }

    let podAgeMinutes (pod: PodInfo) =
        pod.StartTime |> Option.map (fun st -> int (time.GetUtcNow() - st).TotalMinutes)

    member _.GetState(gameId: string) : Task<int * PodInfo option * NodeInfo option * PlayersProbeResult> =
        task {
            let game = gameConfig gameId
            let! desired = k8s.GetDesiredReplicas(game.Namespace, game.Deployment)
            let! pods = k8s.ListPods(game.Namespace, game.PodSelector)
            let pod = pods |> List.filter (fun p -> not p.Terminating) |> List.tryHead
            let! node =
                match pod |> Option.bind (fun p -> p.NodeName) with
                | None -> Task.FromResult None
                | Some nodeName ->
                    task {
                        let! nodes = k8s.ListNodes game.NodeLabelSelector
                        return nodes |> List.tryFind (fun n -> n.Name = nodeName)
                    }
            // Player probes only run against a Ready pod — otherwise there's nothing
            // listening on the game port yet, and NotConfigured doubles as "not probed".
            let! players =
                match pod with
                | Some p when p.Ready ->
                    task {
                        try return! PlayerProbe.probe game
                        with ex -> return PlayersProbeResult.Error ex.Message
                    }
                | _ -> Task.FromResult PlayersProbeResult.NotConfigured
            return desired, pod, node, players
        }

    member this.Status(gameId: string) : Task<string> =
        task {
            let game = gameConfig gameId
            let! desired, pod, node, players = this.GetState gameId
            return StatusText.formatStatus game desired pod node players (time.GetUtcNow())
        }

    member this.Start(gameId: string) : Task<string> =
        task {
            let sem = lockFor gameId
            do! sem.WaitAsync()
            try
                let game = gameConfig gameId
                let! desired = k8s.GetDesiredReplicas(game.Namespace, game.Deployment)
                if desired > 0 then
                    let! pods = k8s.ListPods(game.Namespace, game.PodSelector)
                    let ready = pods |> List.exists (fun p -> not p.Terminating && p.Ready)
                    if ready then
                        return "Already running."
                    else
                        // Already scaled up from an earlier /start (or a pod restart
                        // wiped the in-memory watcher) — make sure one is still running.
                        this.EnsureWatcher gameId
                        return "Already starting."
                else
                    do! k8s.ScaleDeployment(game.Namespace, game.Deployment, 1)
                    this.EnsureWatcher gameId
                    return $"Starting {game.DisplayName}, the node takes a few minutes. I'll post here once it's ready."
            finally
                %sem.Release()
        }

    member _.Stop(gameId: string) : Task<string> =
        task {
            let sem = lockFor gameId
            do! sem.WaitAsync()
            try
                let game = gameConfig gameId
                let! desired = k8s.GetDesiredReplicas(game.Namespace, game.Deployment)
                if desired = 0 then
                    return "Already stopped."
                else
                    let! players =
                        task {
                            try return! PlayerProbe.probe game
                            with ex -> return PlayersProbeResult.Error ex.Message
                        }
                    do! k8s.ScaleDeployment(game.Namespace, game.Deployment, 0)
                    return $"Stopping {game.DisplayName}. Players online at shutdown: {StatusText.formatPlayersAtShutdown players}."
            finally
                %sem.Release()
        }

    /// Starts the (single, deduplicated) start-timeout watcher for a game — a no-op
    /// if one is already running.
    member private this.EnsureWatcher(gameId: string) : unit =
        if watching.TryAdd(gameId, true) then
            fireAndForget logger "fizruk.watch_start" (fun () ->
                task {
                    try do! this.RunWatcher gameId
                    finally watching.TryRemove(gameId) |> ignore
                } :> Task)

    /// Re-arms a start watcher for every game already scaled up but not yet Ready —
    /// call once at startup so a mid-start pod restart still gets its notification.
    member this.ArmPendingWatchers() : Task<unit> =
        task {
            for gameId in config.Games.Keys do
                let! desired, pod, _node, _players = this.GetState gameId
                let podReady = pod |> Option.map (fun p -> p.Ready) |> Option.defaultValue false
                if desired > 0 && not podReady then
                    this.EnsureWatcher gameId
        }

    member private this.RunWatcher(gameId: string) : Task =
        task {
            let game = gameConfig gameId
            let totalSeconds = game.StartTimeoutMinutes * 60
            let intervalSeconds = max 1 (int pollInterval.TotalSeconds)
            let maxIterations = (totalSeconds + intervalSeconds - 1) / intervalSeconds
            let mutable finished = false
            let mutable i = 0
            while not finished && i < maxIterations do
                do! Task.Delay pollInterval
                i <- i + 1
                // Replicas-dropped is checked before pod-ready: a manual /stop during
                // startup must win over a pod that happens to turn Ready moments later.
                let! desired, pod, _node, _players = this.GetState gameId
                if desired = 0 then
                    do! notify gameId $"Start cancelled, {game.DisplayName} stopped"
                    finished <- true
                else
                    match pod with
                    | Some p when p.Ready ->
                        do! notify gameId $"{game.DisplayName} ready at {game.Address}"
                        finished <- true
                    | _ -> ()
            if not finished then
                do! notify gameId $"{game.DisplayName} did not become ready within {game.StartTimeoutMinutes} minutes, check /status."
        } :> Task

    /// One idle-check pass for a single game: pure IdleDecision.decide fed with a
    /// fresh player probe and a window of recent activity-log lines.
    member _.CheckIdle(gameId: string) : Task<unit> =
        task {
            let sem = lockFor gameId
            do! sem.WaitAsync()
            try
                let game = gameConfig gameId
                let! desired = k8s.GetDesiredReplicas(game.Namespace, game.Deployment)
                let! pods = k8s.ListPods(game.Namespace, game.PodSelector)
                let pod = pods |> List.filter (fun p -> not p.Terminating) |> List.tryHead
                let podReady = pod |> Option.map (fun p -> p.Ready) |> Option.defaultValue false
                let ageMinutes = pod |> Option.bind podAgeMinutes

                let! playersProbe =
                    if podReady then
                        task {
                            try return! PlayerProbe.probe game
                            with ex -> return PlayersProbeResult.Error ex.Message
                        }
                    else
                        Task.FromResult PlayersProbeResult.NotConfigured
                let playersCount =
                    match playersProbe with
                    | PlayersProbeResult.Ok info -> IdleDecision.PlayersCount.Ok(PlayersInfo.count info)
                    | PlayersProbeResult.Error e -> IdleDecision.PlayersCount.Error e
                    | PlayersProbeResult.NotConfigured -> IdleDecision.PlayersCount.NotConfigured

                let! activity =
                    match pod with
                    | Some p when podReady ->
                        task {
                            try
                                let! log = k8s.GetPodLog(game.Namespace, p.Name, game.Container, game.IdleWindowMinutes * 60)
                                let events =
                                    log.Split '\n' |> Array.filter game.ActivityRegex.IsMatch |> List.ofArray
                                return IdleDecision.Activity.Ok events
                            with ex -> return IdleDecision.Activity.Error ex.Message
                        }
                    | _ -> Task.FromResult(IdleDecision.Activity.Ok [])

                match IdleDecision.decide desired podReady ageMinutes game.IdleGraceMinutes playersCount activity with
                | IdleDecision.Keep reason ->
                    logger.LogDebug("Fizruk idle-check: keeping {Game} ({Reason})", gameId, reason)
                | IdleDecision.Stop ->
                    do! k8s.ScaleDeployment(game.Namespace, game.Deployment, 0)
                    Metrics.idleStopTotal.Add(1L, Collections.Generic.KeyValuePair("game", box gameId))
                    do! notify gameId $"Stopped {game.DisplayName}: nobody online for the last {game.IdleWindowMinutes} min (was up {StatusText.formatUpFor ageMinutes})."
            finally
                %sem.Release()
        }

    member this.CheckAllIdle() : Task<unit> =
        task {
            for gameId in config.Games.Keys do
                try do! this.CheckIdle gameId
                with ex -> logger.LogError(ex, "Fizruk idle-check failed for {Game}", gameId)
        }
