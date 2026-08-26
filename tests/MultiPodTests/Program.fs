namespace MultiPodTests

open Xunit

[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
[<assembly: AssemblyFixture(typeof<VahterMultiPodContainers>)>]
[<assembly: AssemblyFixture(typeof<CouponMultiPodContainers>)>]
do ()
