namespace Fizruk.Tests

open Xunit
open Fizruk.Tests.ContainerFixture

[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
[<assembly: AssemblyFixture(typeof<FizrukContainerFixture>)>]
do ()
