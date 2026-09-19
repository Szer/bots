namespace Fizruk

open System.Collections.Concurrent
open System.Threading.Tasks

/// Fake IK8sGateway with no cluster dependency: every deployment reads as
/// desired=0/no pods/no nodes. Selected via FIZRUK_FAKE_K8S=true for container tests.
type InMemoryK8sGateway() =
    // (namespace, game id) -> Programmed. Presence in the map is "ListenerSet exists".
    let listenerSets = ConcurrentDictionary<string * string, bool>()

    interface IK8sGateway with
        member _.GetDesiredReplicas(_ns, _deployment) = Task.FromResult 0
        member _.ScaleDeployment(_ns, _deployment, _replicas) = Task.FromResult()
        member _.ListPods(_ns, _labelSelector) = Task.FromResult []
        member _.GetPodLog(_ns, _podName, _container, _sinceSeconds) = Task.FromResult ""
        member _.ListNodes(_labelSelector) = Task.FromResult []

        member _.EnsureListenerSet(game) =
            task {
                match game.Listeners with
                | [] -> ()
                | _ -> listenerSets.TryAdd((game.Namespace, game.Id), false) |> ignore
            }

        member _.DeleteListenerSet(game) =
            task { listenerSets.TryRemove((game.Namespace, game.Id)) |> ignore }

        member _.GetListenerSetStatus(game) =
            task {
                match game.Listeners with
                | [] -> return ListenerSetStatus.Absent
                | _ ->
                    match listenerSets.TryGetValue((game.Namespace, game.Id)) with
                    | false, _ -> return ListenerSetStatus.Absent
                    | true, programmed ->
                        return ListenerSetStatus.Present(accepted = true, programmed = programmed, listeners = Map.empty)
            }

    /// Test hook: flips a previously-created ListenerSet's Programmed condition, as
    /// if the gateway controller had reconciled it.
    member _.SetListenerProgrammed(ns: string, gameId: string, programmed: bool) =
        listenerSets.[(ns, gameId)] <- programmed
