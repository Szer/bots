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
      ActivityRegex: Regex
      IdleGraceMinutes: int
      IdleWindowMinutes: int
      StartTimeoutMinutes: int }

type FizrukConfig =
    { Games: Map<string, GameConfig>
      Chats: Map<int64, string list> }

/// Raised for any config file/schema problem. Program.fs lets this crash startup.
exception ConfigError of string

module Config =

    let noProbe =
        { Type = ProbeType.None; Host = ""; Port = 0; PasswordEnv = None }

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

    let private parseGame (id: string) (el: JsonElement) : GameConfig =
        let ctx = $"games.{id}"
        let regexStr = requireString el ctx "activityRegex"
        let regex =
            try Regex(regexStr, RegexOptions.Compiled)
            with :? ArgumentException as ex ->
                raise (ConfigError $"{ctx}: invalid activityRegex '{regexStr}': {ex.Message}")
        { Id = id
          DisplayName = requireString el ctx "displayName"
          Namespace = requireString el ctx "namespace"
          Deployment = requireString el ctx "deployment"
          PodSelector = requireString el ctx "podSelector"
          Container = requireString el ctx "container"
          NodeLabelSelector = requireString el ctx "nodeLabelSelector"
          Address = requireString el ctx "address"
          Players = parsePlayers ctx (prop el "players")
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
            let games =
                (requireProp root "$" "games").EnumerateObject()
                |> Seq.map (fun p -> p.Name, parseGame p.Name p.Value)
                |> Map.ofSeq
            let chats = parseChats games (requireProp root "$" "chats")
            { Games = games; Chats = chats }
        with
        | ConfigError _ as ex -> raise ex
        | ex -> raise (ConfigError $"failed to parse config: {ex.Message}")

    let loadFromFile (path: string) : FizrukConfig =
        let json =
            try IO.File.ReadAllText path
            with ex -> raise (ConfigError $"failed to read config file '{path}': {ex.Message}")
        parse json
