module Fizruk.Tests.Fakes

open System.Collections.Concurrent
open System.Threading.Tasks
open Fizruk

/// Three games (RCON, RakNet, no-probe) and chats covering every ACL shape tests
/// need: single-game shortcut, multi-game ambiguity, and an unlisted chat id.
let sampleConfigJson =
    """
    {
      "gateway": { "name": "main-gateway", "namespace": "gateway-system" },
      "games": {
        "factorio": {
          "displayName": "Factorio",
          "namespace": "games",
          "deployment": "factorio",
          "podSelector": "app=factorio",
          "container": "factorio",
          "nodeLabelSelector": "workload=game",
          "address": "factorio.szer.dev:34197",
          "players": { "type": "rcon", "host": "factorio.games.svc.cluster.local", "port": 27015, "passwordEnv": "FACTORIO_RCON_PASSWORD" },
          "listener": { "port": 34197, "protocol": "UDP" },
          "activityRegex": "\\[(JOIN|LEAVE|CHAT)\\]",
          "idleGraceMinutes": 45,
          "idleWindowMinutes": 60,
          "startTimeoutMinutes": 1
        },
        "minecraft-creative": {
          "displayName": "Minecraft Creative",
          "namespace": "games",
          "deployment": "minecraft-creative",
          "podSelector": "app=minecraft-creative",
          "container": "minecraft-server",
          "nodeLabelSelector": "workload=game",
          "address": "mc.szer.dev:19132",
          "players": { "type": "raknet", "host": "minecraft-creative.games.svc.cluster.local", "port": 19132 },
          "activityRegex": "Player (connected|disconnected)",
          "idleGraceMinutes": 45,
          "idleWindowMinutes": 60,
          "startTimeoutMinutes": 1
        },
        "no-probe-game": {
          "displayName": "No Probe Game",
          "namespace": "games",
          "deployment": "no-probe-game",
          "podSelector": "app=no-probe-game",
          "container": "server",
          "nodeLabelSelector": "workload=game",
          "address": "noprobe.szer.dev:1234",
          "activityRegex": "\\[(JOIN|LEAVE)\\]",
          "idleGraceMinutes": 45,
          "idleWindowMinutes": 60,
          "startTimeoutMinutes": 1
        },
        "listener-game": {
          "displayName": "Listener Game",
          "namespace": "games",
          "deployment": "listener-game",
          "podSelector": "app=listener-game",
          "container": "server",
          "nodeLabelSelector": "workload=game",
          "address": "listener.szer.dev:5000",
          "listener": { "port": 5000, "protocol": "UDP" },
          "activityRegex": "\\[(JOIN|LEAVE)\\]",
          "idleGraceMinutes": 45,
          "idleWindowMinutes": 60,
          "startTimeoutMinutes": 1
        }
      },
      "chats": {
        "-100": ["factorio"],
        "-200": ["factorio", "minecraft-creative"],
        "-300": ["no-probe-game"],
        "-400": ["listener-game"]
      }
    }
    """

let sampleConfig () = Config.parse sampleConfigJson

/// In-memory IK8sGateway double: per-(namespace,name) desired-replicas/pods/log/
/// ListenerSet state that tests set up directly, no network or cluster involved.
type FakeK8sGateway() =
    let replicas = ConcurrentDictionary<string * string, int>()
    let pods = ConcurrentDictionary<string * string, PodInfo list>()
    let nodes = ConcurrentDictionary<string, NodeInfo list>()
    let logs = ConcurrentDictionary<string * string, string>()
    let listenerSets = ConcurrentDictionary<string * string, bool * bool>() // (accepted, programmed)
    let ensureListenerCalls = ConcurrentQueue<string>()
    let deleteListenerCalls = ConcurrentQueue<string>()
    let mutable ensureListenerError: string option = None
    // Tags every ScaleDeployment/EnsureListenerSet/DeleteListenerSet call in call
    // order, so a test can assert e.g. "ensure ran before scale-up".
    let callLog = ConcurrentQueue<string>()

    member _.SetReplicas(ns, deployment, n) = replicas.[(ns, deployment)] <- n
    member _.SetPods(ns, labelSelector, ps) = pods.[(ns, labelSelector)] <- ps
    member _.SetNodes(labelSelector, ns_) = nodes.[labelSelector] <- ns_
    member _.SetLog(ns, podName, text) = logs.[(ns, podName)] <- text
    member _.CurrentReplicas(ns, deployment) = replicas.GetOrAdd((ns, deployment), 0)

    /// Sets a game's ListenerSet Accepted/Programmed, creating it if absent —
    /// simulates the gateway controller reconciling it.
    member _.SetListenerSetStatus(ns, gameId, accepted, programmed) =
        listenerSets.[(ns, gameId)] <- (accepted, programmed)

    member _.EnsureListenerSetCalls = ensureListenerCalls |> List.ofSeq
    member _.DeleteListenerSetCalls = deleteListenerCalls |> List.ofSeq
    member _.CallLog = callLog |> List.ofSeq

    /// Makes every subsequent EnsureListenerSet call throw instead of succeeding.
    member _.FailEnsureListenerSetWith(message: string) = ensureListenerError <- Some message

    interface IK8sGateway with
        member _.GetDesiredReplicas(ns, deployment) = task { return replicas.GetOrAdd((ns, deployment), 0) }

        member _.ScaleDeployment(ns, deployment, n) =
            task {
                callLog.Enqueue $"scale:{deployment}:{n}"
                replicas.[(ns, deployment)] <- n
            }

        member _.ListPods(ns, labelSelector) =
            task {
                match pods.TryGetValue((ns, labelSelector)) with
                | true, v -> return v
                | false, _ -> return []
            }

        member _.GetPodLog(ns, podName, _container, _sinceSeconds) =
            task {
                match logs.TryGetValue((ns, podName)) with
                | true, v -> return v
                | false, _ -> return ""
            }

        member _.ListNodes(labelSelector) =
            task {
                match nodes.TryGetValue labelSelector with
                | true, v -> return v
                | false, _ -> return []
            }

        member _.EnsureListenerSet(game) =
            task {
                ensureListenerCalls.Enqueue game.Id
                callLog.Enqueue $"ensure:{game.Id}"
                match ensureListenerError with
                | Some msg -> failwith msg
                | None -> listenerSets.TryAdd((game.Namespace, game.Id), (false, false)) |> ignore
            }

        member _.DeleteListenerSet(game) =
            task {
                deleteListenerCalls.Enqueue game.Id
                callLog.Enqueue $"delete:{game.Id}"
                listenerSets.TryRemove((game.Namespace, game.Id)) |> ignore
            }

        member _.GetListenerSetStatus(game) =
            task {
                match listenerSets.TryGetValue((game.Namespace, game.Id)) with
                | false, _ -> return ListenerSetStatus.Absent
                | true, (accepted, programmed) -> return ListenerSetStatus.Present(accepted, programmed)
            }

/// Records every notification instead of calling Telegram.
type FakeNotifier() =
    let sent = ConcurrentQueue<int64 list * string>()
    member _.Sent = sent |> List.ofSeq
    interface INotifier with
        member _.NotifyChats(chatIds, text) =
            sent.Enqueue(chatIds, text)
            Task.FromResult()
