module Fizruk.Tests.ListenerSetFlowTests

open System
open System.Threading.Tasks
open Fizruk
open Fizruk.Tests.Fakes
open Microsoft.Extensions.Logging.Abstractions
open Xunit

let private ns = "games"
let private listenerGameId = "listener-game"
let private listenerDeployment = "listener-game"
let private listenerPodSelector = "app=listener-game"
let private noProbeGameId = "no-probe-game"
let private noProbeDeployment = "no-probe-game"
let private noProbePodSelector = "app=no-probe-game"

let private readyPod (name: string) (startedMinutesAgo: int) =
    { Name = name
      Phase = "Running"
      Ready = true
      Terminating = false
      StartTime = Some(DateTimeOffset.UtcNow.AddMinutes(float -startedMinutesAgo))
      NodeName = Some "node-1" }

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
    GameCore(sampleConfig (), k8sGateway, notifier, TimeProvider.System, pollInterval, NullLogger<GameCore>.Instance)

[<Fact>]
let ``start creates the ListenerSet before scaling up`` () =
    task {
        let k8s = FakeK8sGateway()
        let core = newCore k8s (FakeNotifier()) (TimeSpan.FromSeconds 30.0)
        let! reply = core.Start listenerGameId
        Assert.StartsWith("Starting Listener Game", reply)
        Assert.Equal<string list>([ "ensure:listener-game"; "scale:listener-game:1" ], k8s.CallLog)
    }

[<Fact>]
let ``start aborts on ListenerSet create failure without scaling`` () =
    task {
        let k8s = FakeK8sGateway()
        k8s.FailEnsureListenerSetWith "gateway unavailable"
        let core = newCore k8s (FakeNotifier()) (TimeSpan.FromSeconds 30.0)
        let! reply = core.Start listenerGameId
        Assert.Equal("Could not open the public port: gateway unavailable", reply)
        Assert.Equal(0, k8s.CurrentReplicas(ns, listenerDeployment))
    }

[<Fact>]
let ``a game without a listener never calls EnsureListenerSet or DeleteListenerSet`` () =
    task {
        let k8s = FakeK8sGateway()
        let core = newCore k8s (FakeNotifier()) (TimeSpan.FromSeconds 30.0)
        let! _ = core.Start noProbeGameId
        k8s.SetPods(ns, noProbePodSelector, [ readyPod "no-probe-game-1" 5 ])
        let! _ = core.Stop noProbeGameId
        Assert.Empty(k8s.EnsureListenerSetCalls)
        Assert.Empty(k8s.DeleteListenerSetCalls)
    }

[<Fact>]
let ``the start watcher waits for the ListenerSet to be Programmed before posting ready`` () =
    task {
        let k8s = FakeK8sGateway()
        let notifier = FakeNotifier()
        let core = newCore k8s notifier (TimeSpan.FromMilliseconds 20.0)
        let! _ = core.Start listenerGameId
        k8s.SetPods(ns, listenerPodSelector, [ readyPod "listener-game-1" 0 ])

        // Pod is Ready but the ListenerSet isn't Programmed yet — no "ready" post.
        do! Task.Delay 200
        Assert.DoesNotContain(notifier.Sent, fun (_, text) -> text.Contains "ready at")

        k8s.SetListenerSetStatus(ns, listenerGameId, true, true)
        let! found = waitUntil 5000 (fun () -> notifier.Sent |> List.exists (fun (_, text) -> text.Contains "ready at"))
        Assert.True(found, "expected a ready notification once the ListenerSet was Programmed")
        let _, text = notifier.Sent |> List.find (fun (_, text) -> text.Contains "ready at")
        Assert.Equal("Listener Game ready at listener.szer.dev:5000", text)
    }

[<Fact>]
let ``stop deletes the ListenerSet and reports no cleanup failure`` () =
    task {
        let k8s = FakeK8sGateway()
        k8s.SetReplicas(ns, listenerDeployment, 1)
        k8s.SetPods(ns, listenerPodSelector, [ readyPod "listener-game-1" 5 ])
        k8s.SetListenerSetStatus(ns, listenerGameId, true, true)
        let core = newCore k8s (FakeNotifier()) (TimeSpan.FromSeconds 30.0)
        let! reply = core.Stop listenerGameId
        Assert.DoesNotContain("cleanup failed", reply)
        Assert.Equal<string list>([ listenerGameId ], k8s.DeleteListenerSetCalls)
    }

[<Fact>]
let ``idle check stops an idle game with a listener and deletes its ListenerSet`` () =
    task {
        let k8s = FakeK8sGateway()
        let notifier = FakeNotifier()
        k8s.SetReplicas(ns, listenerDeployment, 1)
        k8s.SetPods(ns, listenerPodSelector, [ readyPod "listener-game-1" 999 ])
        k8s.SetLog(ns, "listener-game-1", "nothing interesting here\n")
        k8s.SetListenerSetStatus(ns, listenerGameId, true, true)
        let core = newCore k8s notifier (TimeSpan.FromSeconds 30.0)
        do! core.CheckIdle listenerGameId
        Assert.Equal(0, k8s.CurrentReplicas(ns, listenerDeployment))
        Assert.Equal<string list>([ listenerGameId ], k8s.DeleteListenerSetCalls)
        let _, text = notifier.Sent.Head
        Assert.DoesNotContain("cleanup failed", text)
    }

[<Fact>]
let ``ReconcileListenerSets ensures a scaled-up game and deletes a scaled-down one`` () =
    task {
        let k8s = FakeK8sGateway()
        // "factorio" is scaled up, "listener-game" is scaled down — one game each way.
        k8s.SetReplicas(ns, "factorio", 1)
        k8s.SetReplicas(ns, listenerDeployment, 0)
        let core = newCore k8s (FakeNotifier()) (TimeSpan.FromSeconds 30.0)
        do! core.ReconcileListenerSets()
        Assert.Equal<string list>([ "factorio" ], k8s.EnsureListenerSetCalls)
        Assert.Equal<string list>([ listenerGameId ], k8s.DeleteListenerSetCalls)
    }

[<Fact>]
let ``status reports the public port state for a game with a listener`` () =
    task {
        let k8s = FakeK8sGateway()
        let core = newCore k8s (FakeNotifier()) (TimeSpan.FromSeconds 30.0)
        let! stoppedText = core.Status listenerGameId
        Assert.Equal("Listener Game: stopped.\nPublic port 5000/UDP: closed", stoppedText)

        k8s.SetReplicas(ns, listenerDeployment, 1)
        k8s.SetListenerSetStatus(ns, listenerGameId, true, false)
        let! openingText = core.Status listenerGameId
        Assert.Contains("Public port 5000/UDP: opening", openingText)

        k8s.SetListenerSetStatus(ns, listenerGameId, true, true)
        let! openText = core.Status listenerGameId
        Assert.Contains("Public port 5000/UDP: open", openText)
    }
