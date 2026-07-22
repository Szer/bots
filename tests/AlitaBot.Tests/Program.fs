namespace AlitaBot.Tests

open Xunit

[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
[<assembly: AssemblyFixture(typeof<AlitaTestContainers>)>]
do ()
