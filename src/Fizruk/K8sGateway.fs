namespace Fizruk

open System
open System.Collections.Generic
open System.IO
open System.Threading.Tasks
open k8s
open k8s.Models

/// Seam over the Kubernetes API so GameCore is testable with a fake. Every member is
/// the minimal read/patch Fizruk needs — no watches, no generic dynamic client.
type IK8sGateway =
    abstract GetDesiredReplicas: ns: string * deployment: string -> Task<int>
    abstract ScaleDeployment: ns: string * deployment: string * replicas: int -> Task<unit>
    abstract ListPods: ns: string * labelSelector: string -> Task<PodInfo list>
    abstract GetPodLog: ns: string * podName: string * container: string * sinceSeconds: int -> Task<string>
    abstract ListNodes: labelSelector: string -> Task<NodeInfo list>

/// K8sGateway-internal helpers, exposed for unit testing without a cluster.
module K8sGateway =

    /// DateTimeOffset from a k8s-deserialized DateTime, treating Unspecified Kind
    /// as UTC (as k8s API timestamps always are) instead of the local offset.
    let asUtcOffset (dt: DateTime) : DateTimeOffset =
        let utc = if dt.Kind = DateTimeKind.Unspecified then DateTime.SpecifyKind(dt, DateTimeKind.Utc) else dt
        DateTimeOffset utc

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
