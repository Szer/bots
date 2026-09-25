namespace BotInfra.Tests

open Xunit
open BotInfra.Tests.PostgresFixture

// The span listener in EventStoreSnapshotTests is process-global — keep the suites sequential.
[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
[<assembly: AssemblyFixture(typeof<PostgresFixture>)>]
do ()
