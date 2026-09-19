module Fizruk.Tests.ListenerSetTests

open System.Text.Json
open Fizruk
open Fizruk.Tests.Fakes
open Xunit

let private gateway: GatewayConfig = { Name = "main-gateway"; Namespace = "gateway-system" }

let private udpGame = (sampleConfig ()).Games.["factorio"]

let private tcpGame =
    { udpGame with
        Id = "tcp-game"
        Namespace = "other-ns"
        Listener = Some { Port = 25565; Protocol = ListenerProtocol.TCP; Gateway = gateway } }

[<Fact>]
let ``build produces the exact UDP ListenerSet manifest`` () =
    let json = JsonSerializer.Serialize(ListenerSet.build udpGame)
    use doc = JsonDocument.Parse json
    let root = doc.RootElement
    Assert.Equal("gateway.networking.k8s.io/v1", root.GetProperty("apiVersion").GetString())
    Assert.Equal("ListenerSet", root.GetProperty("kind").GetString())

    let metadata = root.GetProperty("metadata")
    Assert.Equal("factorio", metadata.GetProperty("name").GetString())
    Assert.Equal("games", metadata.GetProperty("namespace").GetString())
    let labels = metadata.GetProperty("labels")
    Assert.Equal("fizruk", labels.GetProperty("app.kubernetes.io/managed-by").GetString())
    Assert.Equal("factorio", labels.GetProperty("fizruk.szer.dev/game").GetString())

    let parentRef = root.GetProperty("spec").GetProperty("parentRef")
    Assert.Equal("gateway.networking.k8s.io", parentRef.GetProperty("group").GetString())
    Assert.Equal("Gateway", parentRef.GetProperty("kind").GetString())
    Assert.Equal("gateway-system", parentRef.GetProperty("namespace").GetString())
    Assert.Equal("main-gateway", parentRef.GetProperty("name").GetString())

    let listeners = root.GetProperty("spec").GetProperty("listeners")
    Assert.Equal(1, listeners.GetArrayLength())
    let listener = listeners.[0]
    Assert.Equal("factorio-udp", listener.GetProperty("name").GetString())
    Assert.Equal(34197, listener.GetProperty("port").GetInt32())
    Assert.Equal("UDP", listener.GetProperty("protocol").GetString())

    let allowedRoutes = listener.GetProperty("allowedRoutes")
    Assert.Equal("Same", allowedRoutes.GetProperty("namespaces").GetProperty("from").GetString())
    let kinds = allowedRoutes.GetProperty("kinds")
    Assert.Equal(1, kinds.GetArrayLength())
    Assert.Equal("gateway.networking.k8s.io", kinds.[0].GetProperty("group").GetString())
    Assert.Equal("UDPRoute", kinds.[0].GetProperty("kind").GetString())

[<Fact>]
let ``build produces the exact TCP ListenerSet manifest`` () =
    let json = JsonSerializer.Serialize(ListenerSet.build tcpGame)
    use doc = JsonDocument.Parse json
    let listener = doc.RootElement.GetProperty("spec").GetProperty("listeners").[0]
    Assert.Equal("tcp-game-tcp", listener.GetProperty("name").GetString())
    Assert.Equal(25565, listener.GetProperty("port").GetInt32())
    Assert.Equal("TCP", listener.GetProperty("protocol").GetString())
    Assert.Equal(
        "TCPRoute",
        listener.GetProperty("allowedRoutes").GetProperty("kinds").[0].GetProperty("kind").GetString())
