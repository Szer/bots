module Fizruk.Tests.K8sGatewayTests

open System
open Fizruk
open Xunit

[<Fact>]
let ``asUtcOffset treats an Unspecified-Kind DateTime as UTC, not local`` () =
    let dt = DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Unspecified)
    let result = K8sGateway.asUtcOffset dt
    Assert.Equal(TimeSpan.Zero, result.Offset)
    Assert.Equal(DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc), result.UtcDateTime)

[<Fact>]
let ``asUtcOffset preserves an already-UTC DateTime`` () =
    let dt = DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc)
    let result = K8sGateway.asUtcOffset dt
    Assert.Equal(TimeSpan.Zero, result.Offset)
    Assert.Equal(dt, result.UtcDateTime)
