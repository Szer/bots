namespace Fizruk

open System
open System.Text.Json
open System.Text.RegularExpressions

/// How to reach a game server's player list. `None` means no live probe is configured.
[<RequireQualifiedAccess>]
type ProbeType =
    | None
    | Rcon
    | RakNet

type PlayersConfig =
    { Type: ProbeType
      Host: string
      Port: int
      /// Env var holding the RCON password. Set only when Type = Rcon.
      PasswordEnv: string option }

/// The shared Envoy Gateway a game's ListenerSet attaches to.
type GatewayConfig = { Name: string; Namespace: string }

[<RequireQualifiedAccess>]
type ListenerProtocol =
    | UDP
    | TCP

/// One of a game's on-demand public ports; `Name` is stable and referenced by
/// UDPRoute/TCPRoute manifests, so it must survive config reordering.
type ListenerConfig =
    { Name: string
      Port: int
      Protocol: ListenerProtocol }

type GameConfig =
    { Id: string
      DisplayName: string
      Namespace: string
      Deployment: string
      PodSelector: string
      Container: string
      NodeLabelSelector: string
      Address: string
      Players: PlayersConfig
      /// Empty when the game has no public port. `Gateway` is `Some` iff non-empty.
      Listeners: ListenerConfig list
      Gateway: GatewayConfig option
      ActivityRegex: Regex
      IdleGraceMinutes: int
      IdleWindowMinutes: int
      StartTimeoutMinutes: int }

type FizrukConfig =
    { Games: Map<string, GameConfig>
      Chats: Map<int64, string list>
      Gateway: GatewayConfig option }

/// Raised for any config file/schema problem. Program.fs lets this crash startup.
exception ConfigError of string

module Config =

    let noProbe =
        { Type = ProbeType.None; Host = ""; Port = 0; PasswordEnv = None }

    /// "UDP"/"TCP" — the ListenerSet manifest's `protocol` value and the /status text.
    let protocolText (protocol: ListenerProtocol) : string =
        match protocol with
        | ListenerProtocol.UDP -> "UDP"
        | ListenerProtocol.TCP -> "TCP"

    let private prop (el: JsonElement) (name: string) : JsonElement option =
        match el.TryGetProperty name with
        | true, v -> Some v
        | false, _ -> None

    let private requireProp (el: JsonElement) (ctx: string) (name: string) : JsonElement =
        match prop el name with
        | Some v -> v
        | None -> raise (ConfigError $"{ctx}: missing required field '{name}'")

    let private requireString (el: JsonElement) (ctx: string) (name: string) : string =
        (requireProp el ctx name).GetString()

    let private requireInt (el: JsonElement) (ctx: string) (name: string) : int =
        (requireProp el ctx name).GetInt32()

    let private parsePlayers (ctx: string) (playersEl: JsonElement option) : PlayersConfig =
        match playersEl with
        | None -> noProbe
        | Some el ->
            match (requireString el ctx "type").ToLowerInvariant() with
            | "none" -> noProbe
            | "rcon" ->
                let passwordEnv =
                    match prop el "passwordEnv" with
                    | Some v -> v.GetString()
                    | None -> raise (ConfigError $"{ctx}: players.passwordEnv is required when type=rcon")
                { Type = ProbeType.Rcon
                  Host = requireString el ctx "host"
                  Port = requireInt el ctx "port"
                  PasswordEnv = Some passwordEnv }
            | "raknet" ->
                { Type = ProbeType.RakNet
                  Host = requireString el ctx "host"
                  Port = requireInt el ctx "port"
                  PasswordEnv = None }
            | other -> raise (ConfigError $"{ctx}: unknown players.type '{other}'")

    let private parseGateway (el: JsonElement) : GatewayConfig =
        { Name = requireString el "gateway" "name"
          Namespace = requireString el "gateway" "namespace" }

    let private dns1123LabelRegex = Regex(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?$", RegexOptions.Compiled)

    let private parsePort (el: JsonElement) (ctx: string) (label: string) : int =
        let port = requireInt el ctx "port"
        if port < 1 || port > 65535 then
            raise (ConfigError $"{ctx}: {label}.port must be between 1 and 65535, got {port}")
        port

    let private parseProtocol (el: JsonElement) (ctx: string) (label: string) : ListenerProtocol =
        match (requireString el ctx "protocol").ToUpperInvariant() with
        | "UDP" -> ListenerProtocol.UDP
        | "TCP" -> ListenerProtocol.TCP
        | other -> raise (ConfigError $"{ctx}: unknown {label}.protocol '{other}'")

    /// One entry of the canonical `listeners` array — `name` is required and must be
    /// a valid DNS-1123 label, since it's what UDPRoute/TCPRoute manifests reference.
    let private parseListenerEntry (ctx: string) (el: JsonElement) : ListenerConfig =
        let name = requireString el ctx "name"
        if name.Length > 63 || not (dns1123LabelRegex.IsMatch name) then
            raise (ConfigError $"{ctx}: listener name '{name}' must be a valid DNS-1123 label")
        { Name = name; Port = parsePort el ctx "listeners[]"; Protocol = parseProtocol el ctx "listeners[]" }

    /// The deprecated singular `listener` object, reproducing the name it used to
    /// bake into the ListenerSet: `<game>-udp`/`<game>-tcp`.
    let private parseLegacyListener (ctx: string) (gameId: string) (el: JsonElement) : ListenerConfig =
        let protocol = parseProtocol el ctx "listener"
        let suffix = match protocol with ListenerProtocol.UDP -> "udp" | ListenerProtocol.TCP -> "tcp"
        { Name = $"{gameId}-{suffix}"; Port = parsePort el ctx "listener"; Protocol = protocol }

    let private validateListeners (ctx: string) (listeners: ListenerConfig list) : unit =
        listeners
        |> List.map (fun l -> l.Name)
        |> List.countBy id
        |> List.tryFind (fun (_, c) -> c > 1)
        |> Option.iter (fun (n, _) -> raise (ConfigError $"{ctx}: duplicate listener name '{n}'"))
        listeners
        |> List.map (fun l -> l.Port)
        |> List.countBy id
        |> List.tryFind (fun (_, c) -> c > 1)
        |> Option.iter (fun (p, _) -> raise (ConfigError $"{ctx}: duplicate listener port {p}"))

    /// `listener`/`listeners` are mutually exclusive; any non-empty result requires
    /// the top-level `gateway`, the ListenerSet's parentRef target.
    let private parseListeners
        (ctx: string)
        (gameId: string)
        (gateway: GatewayConfig option)
        (legacyEl: JsonElement option)
        (listEl: JsonElement option)
        : ListenerConfig list * GatewayConfig option =
        let listeners =
            match legacyEl, listEl with
            | Some _, Some _ -> raise (ConfigError $"{ctx}: specify either 'listener' or 'listeners', not both")
            | None, None -> []
            | Some el, None -> [ parseLegacyListener ctx gameId el ]
            | None, Some el -> el.EnumerateArray() |> Seq.map (parseListenerEntry ctx) |> List.ofSeq
        if listeners.IsEmpty then
            [], None
        else
            validateListeners ctx listeners
            match gateway with
            | Some g -> listeners, Some g
            | None -> raise (ConfigError $"{ctx}: listeners require a top-level 'gateway' to be configured")

    let private parseGame (gateway: GatewayConfig option) (id: string) (el: JsonElement) : GameConfig =
        let ctx = $"games.{id}"
        let regexStr = requireString el ctx "activityRegex"
        let regex =
            try Regex(regexStr, RegexOptions.Compiled)
            with :? ArgumentException as ex ->
                raise (ConfigError $"{ctx}: invalid activityRegex '{regexStr}': {ex.Message}")
        let listeners, gameGateway = parseListeners ctx id gateway (prop el "listener") (prop el "listeners")
        { Id = id
          DisplayName = requireString el ctx "displayName"
          Namespace = requireString el ctx "namespace"
          Deployment = requireString el ctx "deployment"
          PodSelector = requireString el ctx "podSelector"
          Container = requireString el ctx "container"
          NodeLabelSelector = requireString el ctx "nodeLabelSelector"
          Address = requireString el ctx "address"
          Players = parsePlayers ctx (prop el "players")
          Listeners = listeners
          Gateway = gameGateway
          ActivityRegex = regex
          IdleGraceMinutes = requireInt el ctx "idleGraceMinutes"
          IdleWindowMinutes = requireInt el ctx "idleWindowMinutes"
          StartTimeoutMinutes = requireInt el ctx "startTimeoutMinutes" }

    let private parseChats (games: Map<string, GameConfig>) (el: JsonElement) : Map<int64, string list> =
        el.EnumerateObject()
        |> Seq.map (fun p ->
            let chatId =
                match Int64.TryParse p.Name with
                | true, v -> v
                | false, _ -> raise (ConfigError $"chats: invalid chat id '{p.Name}'")
            let gameIds =
                p.Value.EnumerateArray() |> Seq.map (fun g -> g.GetString()) |> List.ofSeq
            for gid in gameIds do
                if not (games.ContainsKey gid) then
                    raise (ConfigError $"chats.{p.Name}: references unknown game '{gid}'")
            chatId, gameIds)
        |> Map.ofSeq

    /// Parses and validates the full Fizruk config document. Raises ConfigError on any
    /// schema violation, unknown game reference, or invalid regex.
    let parse (json: string) : FizrukConfig =
        try
            use doc = JsonDocument.Parse json
            let root = doc.RootElement
            let gateway = prop root "gateway" |> Option.map parseGateway
            let games =
                (requireProp root "$" "games").EnumerateObject()
                |> Seq.map (fun p -> p.Name, parseGame gateway p.Name p.Value)
                |> Map.ofSeq
            let chats = parseChats games (requireProp root "$" "chats")
            { Games = games; Chats = chats; Gateway = gateway }
        with
        | ConfigError _ as ex -> raise ex
        | ex -> raise (ConfigError $"failed to parse config: {ex.Message}")

    let loadFromFile (path: string) : FizrukConfig =
        let json =
            try IO.File.ReadAllText path
            with ex -> raise (ConfigError $"failed to read config file '{path}': {ex.Message}")
        parse json
