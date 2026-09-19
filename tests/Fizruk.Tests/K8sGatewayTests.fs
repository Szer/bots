module Fizruk.Tests.K8sGatewayTests

open System
open System.Text.Json
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

let private parseStatus (json: string) =
    use doc = JsonDocument.Parse json
    K8sGateway.parseListenerSetStatus doc.RootElement

[<Fact>]
let ``parseListenerSetStatus reads a fresh object with no status yet as not accepted, not programmed`` () =
    Assert.Equal(ListenerSetStatus.Present(accepted = false, programmed = false), parseStatus """{ "metadata": {} }""")

[<Fact>]
let ``parseListenerSetStatus reads Accepted and Programmed True conditions`` () =
    let json =
        """
        { "status": { "conditions": [
            { "type": "Accepted", "status": "True" },
            { "type": "Programmed", "status": "True" } ] } }
        """
    Assert.Equal(ListenerSetStatus.Present(accepted = true, programmed = true), parseStatus json)

[<Fact>]
let ``parseListenerSetStatus treats a False Programmed condition as not programmed`` () =
    let json =
        """
        { "status": { "conditions": [
            { "type": "Accepted", "status": "True" },
            { "type": "Programmed", "status": "False" } ] } }
        """
    Assert.Equal(ListenerSetStatus.Present(accepted = true, programmed = false), parseStatus json)

[<Fact>]
let ``parseListenerSetStatus requires every per-listener Programmed condition when the listeners array is present`` () =
    let json =
        """
        { "status": {
            "conditions": [ { "type": "Programmed", "status": "True" } ],
            "listeners": [
              { "conditions": [ { "type": "Programmed", "status": "True" } ] },
              { "conditions": [ { "type": "Programmed", "status": "False" } ] } ] } }
        """
    Assert.Equal(ListenerSetStatus.Present(accepted = false, programmed = false), parseStatus json)
