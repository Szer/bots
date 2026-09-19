namespace Fizruk

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Text.Json
open System.Threading.Tasks
open k8s
open k8s.Autorest
open k8s.Models

/// Seam over the Kubernetes API so GameCore is testable with a fake. Every member is
/// the minimal read/patch Fizruk needs — no watches, no generic dynamic client.
type IK8sGateway =
    abstract GetDesiredReplicas: ns: string * deployment: string -> Task<int>
    abstract ScaleDeployment: ns: string * deployment: string * replicas: int -> Task<unit>
    abstract ListPods: ns: string * labelSelector: string -> Task<PodInfo list>
    abstract GetPodLog: ns: string * podName: string * container: string * sinceSeconds: int -> Task<string>
    abstract ListNodes: labelSelector: string -> Task<NodeInfo list>
    /// Creates the game's ListenerSet if it has a `Listener` configured. A no-op for
    /// a game with none. HTTP 409 (AlreadyExists) counts as success.
    abstract EnsureListenerSet: game: GameConfig -> Task<unit>
    /// Deletes the game's ListenerSet. HTTP 404 counts as success.
    abstract DeleteListenerSet: game: GameConfig -> Task<unit>
    abstract GetListenerSetStatus: game: GameConfig -> Task<ListenerSetStatus>

/// K8sGateway-internal helpers, exposed for unit testing without a cluster.
module K8sGateway =

    /// DateTimeOffset from a k8s-deserialized DateTime, treating Unspecified Kind
    /// as UTC (as k8s API timestamps always are) instead of the local offset.
    let asUtcOffset (dt: DateTime) : DateTimeOffset =
        let utc = if dt.Kind = DateTimeKind.Unspecified then DateTime.SpecifyKind(dt, DateTimeKind.Utc) else dt
        DateTimeOffset utc

    let private tryProp (el: JsonElement) (name: string) : JsonElement option =
        match el.TryGetProperty name with
        | true, v -> Some v
        | false, _ -> None

    let private hasTrueCondition (conditions: JsonElement option) (condType: string) : bool =
        match conditions with
        | None -> false
        | Some conditions ->
            conditions.EnumerateArray()
            |> Seq.exists (fun c ->
                match tryProp c "type", tryProp c "status" with
                | Some t, Some s -> t.GetString() = condType && s.GetString() = "True"
                | _ -> false)

    /// Reads Accepted/Programmed off a ListenerSet's `status`; when `status.listeners`
    /// is present, overall Programmed also requires every listener's own to be True.
    let parseListenerSetStatus (el: JsonElement) : ListenerSetStatus =
        match tryProp el "status" with
        | None -> ListenerSetStatus.Present(accepted = false, programmed = false)
        | Some status ->
            let conditions = tryProp status "conditions"
            let accepted = hasTrueCondition conditions "Accepted"
            let topProgrammed = hasTrueCondition conditions "Programmed"
            let listenersProgrammed =
                match tryProp status "listeners" with
                | None -> true
                | Some listeners ->
                    listeners.EnumerateArray()
                    |> Seq.forall (fun l -> hasTrueCondition (tryProp l "conditions") "Programmed")
            ListenerSetStatus.Present(accepted, topProgrammed && listenersProgrammed)

/// Real implementation backed by the official KubernetesClient, using in-cluster config.
type KubernetesGateway(client: Kubernetes) =

    let isReady (conditions: IList<V1PodCondition>) =
        not (isNull conditions) && conditions |> Seq.exists (fun c -> c.Type = "Ready" && c.Status = "True")

    let isNodeReady (conditions: IList<V1NodeCondition>) =
        not (isNull conditions) && conditions |> Seq.exists (fun c -> c.Type = "Ready" && c.Status = "True")

    interface IK8sGateway with
        member _.GetDesiredReplicas(ns, deployment) =
            task {
                let! d = client.ReadNamespacedDeploymentAsync(deployment, ns)
                return d.Spec.Replicas |> Option.ofNullable |> Option.defaultValue 0
            }

        member _.ScaleDeployment(ns, deployment, replicas) =
            task {
                let patch = V1Patch({| spec = {| replicas = replicas |} |}, V1Patch.PatchType.MergePatch)
                let! _ = client.PatchNamespacedDeploymentScaleAsync(patch, deployment, ns)
                return ()
            }

        member _.ListPods(ns, labelSelector) =
            task {
                let! list = client.ListNamespacedPodAsync(ns, labelSelector = labelSelector)
                return
                    list.Items
                    |> Seq.map (fun p ->
                        { Name = p.Metadata.Name
                          Phase = p.Status.Phase
                          Ready = isReady p.Status.Conditions
                          Terminating = p.Metadata.DeletionTimestamp.HasValue
                          StartTime = p.Status.StartTime |> Option.ofNullable |> Option.map K8sGateway.asUtcOffset
                          NodeName = p.Spec.NodeName |> Option.ofObj })
                    |> List.ofSeq
            }

        member _.GetPodLog(ns, podName, container, sinceSeconds) =
            task {
                use! stream = client.ReadNamespacedPodLogAsync(podName, ns, container = container, sinceSeconds = sinceSeconds)
                use reader = new StreamReader(stream)
                return! reader.ReadToEndAsync()
            }

        member _.ListNodes(labelSelector) =
            task {
                let! list = client.ListNodeAsync(labelSelector = labelSelector)
                return
                    list.Items
                    |> Seq.map (fun n ->
                        { Name = n.Metadata.Name
                          Ready = isNodeReady n.Status.Conditions
                          CreationTimestamp = n.Metadata.CreationTimestamp |> Option.ofNullable |> Option.map K8sGateway.asUtcOffset })
                    |> List.ofSeq
            }

        member _.EnsureListenerSet(game) =
            task {
                match game.Listener with
                | None -> ()
                | Some _ ->
                    let body = ListenerSet.build game
                    try
                        let! _ =
                            client.CustomObjects.CreateNamespacedCustomObjectAsync<JsonElement>(
                                body, ListenerSet.group, ListenerSet.version, game.Namespace, ListenerSet.plural)
                        ()
                    with :? HttpOperationException as ex when ex.Response.StatusCode = HttpStatusCode.Conflict -> ()
            }

        member _.DeleteListenerSet(game) =
            task {
                match game.Listener with
                | None -> ()
                | Some _ ->
                    try
                        let! _ =
                            client.CustomObjects.DeleteNamespacedCustomObjectAsync<JsonElement>(
                                ListenerSet.group, ListenerSet.version, game.Namespace, ListenerSet.plural, game.Id)
                        ()
                    with :? HttpOperationException as ex when ex.Response.StatusCode = HttpStatusCode.NotFound -> ()
            }

        member _.GetListenerSetStatus(game) =
            task {
                match game.Listener with
                | None -> return ListenerSetStatus.Absent
                | Some _ ->
                    try
                        let! el =
                            client.CustomObjects.GetNamespacedCustomObjectAsync<JsonElement>(
                                ListenerSet.group, ListenerSet.version, game.Namespace, ListenerSet.plural, game.Id)
                        return K8sGateway.parseListenerSetStatus el
                    with :? HttpOperationException as ex when ex.Response.StatusCode = HttpStatusCode.NotFound ->
                        return ListenerSetStatus.Absent
            }
