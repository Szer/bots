module Fizruk.Tests.ConfigTests

open Fizruk
open Fizruk.Tests.Fakes
open Xunit

let private minimalGame (activityRegex: string) (playersJson: string) =
    $$"""
    {
      "games": {
        "g": {
          "displayName": "G",
          "namespace": "ns",
          "deployment": "g",
          "podSelector": "app=g",
          "container": "g",
          "nodeLabelSelector": "workload=game",
          "address": "g.example:1234",
          {{playersJson}}
          "activityRegex": "{{activityRegex}}",
          "idleGraceMinutes": 5,
          "idleWindowMinutes": 10,
          "startTimeoutMinutes": 1
        }
      },
      "chats": { "-1": ["g"] }
    }
    """

[<Fact>]
let ``valid config parses every game and chat`` () =
    let config = sampleConfig ()
    Assert.Equal(3, config.Games.Count)
    Assert.Equal<string list>([ "factorio" ], config.Chats.[-100L])
    Assert.Equal<string list>([ "factorio"; "minecraft-creative" ], config.Chats.[-200L])

[<Fact>]
let ``rcon game requires host, port, and passwordEnv`` () =
    let config = sampleConfig ()
    let game = config.Games.["factorio"]
    Assert.Equal(ProbeType.Rcon, game.Players.Type)
    Assert.Equal(Some "FACTORIO_RCON_PASSWORD", game.Players.PasswordEnv)

[<Fact>]
let ``game without a players block has no probe configured`` () =
    let config = sampleConfig ()
    Assert.Equal(ProbeType.None, config.Games.["no-probe-game"].Players.Type)

[<Fact>]
let ``chat referencing an unknown game raises ConfigError`` () =
    let json = minimalGame "JOIN" ""
    let json = json.Replace("\"chats\": { \"-1\": [\"g\"] }", "\"chats\": { \"-1\": [\"does-not-exist\"] }")
    Assert.Throws<ConfigError>(fun () -> Config.parse json |> ignore) |> ignore

[<Fact>]
let ``missing required field raises ConfigError`` () =
    let json = """{ "games": { "g": { "namespace": "ns" } }, "chats": {} }"""
    Assert.Throws<ConfigError>(fun () -> Config.parse json |> ignore) |> ignore

[<Fact>]
let ``invalid activityRegex raises ConfigError`` () =
    let json = minimalGame "[unterminated" ""
    Assert.Throws<ConfigError>(fun () -> Config.parse json |> ignore) |> ignore

[<Fact>]
let ``rcon players block without passwordEnv raises ConfigError`` () =
    let players = """"players": { "type": "rcon", "host": "h", "port": 1 },"""
    let json = minimalGame "JOIN" players
    Assert.Throws<ConfigError>(fun () -> Config.parse json |> ignore) |> ignore

[<Fact>]
let ``rcon players block with passwordEnv parses`` () =
    let players = """"players": { "type": "rcon", "host": "h", "port": 1, "passwordEnv": "PW" },"""
    let json = minimalGame "JOIN" players
    let config = Config.parse json
    Assert.Equal(Some "PW", config.Games.["g"].Players.PasswordEnv)
