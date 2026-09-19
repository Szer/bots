module Fizruk.Tests.CoreFlowTests

open System
open System.Threading.Tasks
open Fizruk
open Fizruk.Tests.Fakes
open Microsoft.Extensions.Logging.Abstractions
open Xunit

let private ns = "games"
let private deployment = "no-probe-game"
let private podSelector = "app=no-probe-game"
let private nodeLabelSelector = "workload=game"
let private gameId = "no-probe-game"

let private readyPod (name: string) (startedMinutesAgo: int) =
    { Name = name
      Phase = "Running"
      Ready = true
      Terminating = false
      StartTime = Some(DateTimeOffset.UtcNow.AddMinutes(float -startedMinutesAgo))
      NodeName = Some "node-1" }

let private pendingPod =
    { Name = "no-probe-game-x"
      Phase = "Pending"
      Ready = false
      Terminating = false
      StartTime = None
      NodeName = None }

/// GameCore doesn't expose a way to await its fire-and-forget watcher, so tests that
/// depend on it poll the fake notifier for up to `timeoutMs` real milliseconds.
let rec private waitUntil (timeoutMs: int) (check: unit -> bool) : Task<bool> =
    task {
        let sw = Diagnostics.Stopwatch.StartNew()
        let mutable ok = check ()
        while not ok && sw.ElapsedMilliseconds < int64 timeoutMs do
            do! Task.Delay 10
            ok <- check ()
        return ok
    }

let private newCore (k8sGateway: FakeK8sGateway) (notifier: FakeNotifier) (pollInterval: TimeSpan) =
    GameCore(
        sampleConfig (),
        k8sGateway,
        notifier,
        TimeProvider.System,
        pollInterval,
        NullLogger<GameCore>.Instance)

[<Fact>]
let ``start when stopped scales up and replies Starting`` () =
    task {
        let k8s = FakeK8sGateway()
        let core = newCore k8s (FakeNotifier()) (TimeSpan.FromSeconds 30.0)
        let! reply = core.Start gameId
        Assert.StartsWith("Starting No Probe Game", reply)
        Assert.Equal(1, k8s.CurrentReplicas(ns, deployment))
    }

[<Fact>]
let ``start when already starting (pod not ready yet) reports Already starting`` () =
    task {
        let k8s = FakeK8sGateway()
        k8s.SetReplicas(ns, deployment, 1)
        k8s.SetPods(ns, podSelector, [ pendingPod ])
        let core = newCore k8s (FakeNotifier()) (TimeSpan.FromSeconds 30.0)
        let! reply = core.Start gameId
        Assert.Equal("Already starting.", reply)
    }

[<Fact>]
let ``start when already running reports Already running`` () =
    task {
        let k8s = FakeK8sGateway()
        k8s.SetReplicas(ns, deployment, 1)
        k8s.SetPods(ns, podSelector, [ readyPod "no-probe-game-1" 5 ])
        let core = newCore k8s (FakeNotifier()) (TimeSpan.FromSeconds 30.0)
        let! reply = core.Start gameId
        Assert.Equal("Already running.", reply)
    }

[<Fact>]
let ``stop when running scales down and reports shutdown`` () =
    task {
        let k8s = FakeK8sGateway()
        k8s.SetReplicas(ns, deployment, 1)
        k8s.SetPods(ns, podSelector, [ readyPod "no-probe-game-1" 5 ])
        let core = newCore k8s (FakeNotifier()) (TimeSpan.FromSeconds 30.0)
        let! reply = core.Stop gameId
        Assert.Equal("Stopping No Probe Game. Players online at shutdown: unknown.", reply)
        Assert.Equal(0, k8s.CurrentReplicas(ns, deployment))
    }

[<Fact>]
let ``stop when already stopped is a no-op`` () =
    task {
        let k8s = FakeK8sGateway()
        let core = newCore k8s (FakeNotifier()) (TimeSpan.FromSeconds 30.0)
        let! reply = core.Stop gameId
        Assert.Equal("Already stopped.", reply)
    }

[<Fact>]
let ``watcher posts ready once the pod becomes Ready`` () =
    task {
        let k8s = FakeK8sGateway()
        let notifier = FakeNotifier()
        let core = newCore k8s notifier (TimeSpan.FromMilliseconds 20.0)
        let! _ = core.Start gameId
        k8s.SetPods(ns, podSelector, [ readyPod "no-probe-game-1" 0 ])
        let! found = waitUntil 5000 (fun () -> notifier.Sent |> List.exists (fun (_, text) -> text.Contains "ready at"))
        Assert.True(found, "expected a ready notification")
        let _, text = notifier.Sent |> List.find (fun (_, text) -> text.Contains "ready at")
        Assert.Equal("No Probe Game ready at noprobe.szer.dev:1234", text)
    }

[<Fact>]
let ``watcher posts cancelled when replicas drop to zero`` () =
    task {
        let k8s = FakeK8sGateway()
        let notifier = FakeNotifier()
        let core = newCore k8s notifier (TimeSpan.FromMilliseconds 20.0)
        let! _ = core.Start gameId
        let! _ = core.Stop gameId
        let! found = waitUntil 5000 (fun () -> notifier.Sent |> List.exists (fun (_, text) -> text.Contains "Start cancelled"))
        Assert.True(found, "expected a start-cancelled notification")
    }

[<Fact>]
let ``idle check stops an idle game and notifies every controlling chat`` () =
    task {
        let k8s = FakeK8sGateway()
        let notifier = FakeNotifier()
        k8s.SetReplicas(ns, deployment, 1)
        k8s.SetPods(ns, podSelector, [ readyPod "no-probe-game-1" 999 ])
        k8s.SetLog(ns, "no-probe-game-1", "server started\nnothing interesting here\n")
        let core = newCore k8s notifier (TimeSpan.FromSeconds 30.0)
        do! core.CheckIdle gameId
        Assert.Equal(0, k8s.CurrentReplicas(ns, deployment))
        Assert.Single(notifier.Sent) |> ignore
        let chatIds, text = notifier.Sent.Head
        Assert.Equal<int64 list>([ -300L ], chatIds)
        Assert.Contains("Stopped No Probe Game", text)
    }

[<Fact>]
let ``idle check keeps a game within its grace period`` () =
    task {
        let k8s = FakeK8sGateway()
        let notifier = FakeNotifier()
        k8s.SetReplicas(ns, deployment, 1)
        k8s.SetPods(ns, podSelector, [ readyPod "no-probe-game-1" 1 ])
        k8s.SetLog(ns, "no-probe-game-1", "")
        let core = newCore k8s notifier (TimeSpan.FromSeconds 30.0)
        do! core.CheckIdle gameId
        Assert.Equal(1, k8s.CurrentReplicas(ns, deployment))
        Assert.Empty(notifier.Sent)
    }

[<Fact>]
let ``idle check keeps a game with recent matching activity`` () =
    task {
        let k8s = FakeK8sGateway()
        let notifier = FakeNotifier()
        k8s.SetReplicas(ns, deployment, 1)
        k8s.SetPods(ns, podSelector, [ readyPod "no-probe-game-1" 999 ])
        k8s.SetLog(ns, "no-probe-game-1", "[JOIN] alice\n")
        let core = newCore k8s notifier (TimeSpan.FromSeconds 30.0)
        do! core.CheckIdle gameId
        Assert.Equal(1, k8s.CurrentReplicas(ns, deployment))
        Assert.Empty(notifier.Sent)
    }
