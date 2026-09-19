namespace Fizruk

open System.Threading.Tasks

/// Fake IK8sGateway with no cluster dependency: every deployment reads as
/// desired=0/no pods/no nodes. Selected via FIZRUK_FAKE_K8S=true for container tests.
type InMemoryK8sGateway() =
    interface IK8sGateway with
        member _.GetDesiredReplicas(_ns, _deployment) = Task.FromResult 0
        member _.ScaleDeployment(_ns, _deployment, _replicas) = Task.FromResult()
        member _.ListPods(_ns, _labelSelector) = Task.FromResult []
        member _.GetPodLog(_ns, _podName, _container, _sinceSeconds) = Task.FromResult ""
        member _.ListNodes(_labelSelector) = Task.FromResult []
