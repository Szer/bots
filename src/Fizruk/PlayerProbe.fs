namespace Fizruk

open System
open System.Threading.Tasks

/// Dispatches a game's configured player probe (RCON, RakNet, or none).
module PlayerProbe =

    let private timeout = TimeSpan.FromSeconds 5.0

    let probe (game: GameConfig) : Task<PlayersProbeResult> =
        task {
            match game.Players.Type with
            | ProbeType.None -> return PlayersProbeResult.NotConfigured
            | ProbeType.Rcon ->
                match Rcon.passwordFor game.Players with
                | Error e -> return PlayersProbeResult.Error e
                | Ok password ->
                    let! result = Rcon.probeAsync game.Players.Host game.Players.Port password timeout
                    return
                        match result with
                        | Ok names -> PlayersProbeResult.Ok(PlayersInfo.Names names)
                        | Error e -> PlayersProbeResult.Error e
            | ProbeType.RakNet ->
                let! result = RakNet.probeAsync game.Players.Host game.Players.Port timeout
                return
                    match result with
                    | Ok(online, _maxPlayers) -> PlayersProbeResult.Ok(PlayersInfo.Count online)
                    | Error e -> PlayersProbeResult.Error e
        }
