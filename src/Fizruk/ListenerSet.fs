namespace Fizruk

/// Builds the Gateway API `ListenerSet` manifest for a game's public port. Pure
/// (no k8s client, no I/O) so the exact JSON shape can be asserted in tests.
module ListenerSet =

    let group = "gateway.networking.k8s.io"
    let version = "v1"
    let plural = "listenersets"

    type RouteKind = { group: string; kind: string }
    type RoutesNamespaces = { from: string }
    type AllowedRoutes = { namespaces: RoutesNamespaces; kinds: RouteKind list }
    type Listener = { name: string; port: int; protocol: string; allowedRoutes: AllowedRoutes }
    type ParentRef = { group: string; kind: string; ``namespace``: string; name: string }
    type Spec = { parentRef: ParentRef; listeners: Listener list }
    type Metadata = { name: string; ``namespace``: string; labels: Map<string, string> }
    type Body = { apiVersion: string; kind: string; metadata: Metadata; spec: Spec }

    let private routeKind (protocol: ListenerProtocol) =
        match protocol with
        | ListenerProtocol.UDP -> "UDPRoute"
        | ListenerProtocol.TCP -> "TCPRoute"

    /// `game.Listeners` must be non-empty and `game.Gateway` `Some` — callers only
    /// invoke this after checking that.
    let build (game: GameConfig) : Body =
        let gateway = game.Gateway.Value
        { apiVersion = $"{group}/{version}"
          kind = "ListenerSet"
          metadata =
            { name = game.Id
              ``namespace`` = game.Namespace
              labels =
                Map.ofList
                    [ "app.kubernetes.io/managed-by", "fizruk"
                      "fizruk.szer.dev/game", game.Id ] }
          spec =
            { parentRef =
                { group = group
                  kind = "Gateway"
                  ``namespace`` = gateway.Namespace
                  name = gateway.Name }
              listeners =
                game.Listeners
                |> List.map (fun l ->
                    { name = l.Name
                      port = l.Port
                      protocol = Config.protocolText l.Protocol
                      allowedRoutes =
                        { namespaces = { from = "Same" }
                          kinds = [ { group = group; kind = routeKind l.Protocol } ] } }) } }
