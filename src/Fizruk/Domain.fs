namespace Fizruk

open System

/// The first non-terminating pod matching a game's podSelector.
type PodInfo =
    { Name: string
      Phase: string
      Ready: bool
      Terminating: bool
      StartTime: DateTimeOffset option
      NodeName: string option }

type NodeInfo =
    { Name: string
      Ready: bool
      CreationTimestamp: DateTimeOffset option }

/// What a player probe found: named players (RCON) or just a headcount (RakNet).
[<RequireQualifiedAccess>]
type PlayersInfo =
    | Names of string list
    | Count of int

module PlayersInfo =
    let count (info: PlayersInfo) : int =
        match info with
        | PlayersInfo.Names names -> List.length names
        | PlayersInfo.Count n -> n

/// Outcome of probing a game's player list. NotConfigured covers both "no players
/// block in config" and "not probed because the pod isn't Ready yet".
[<RequireQualifiedAccess>]
type PlayersProbeResult =
    | Ok of PlayersInfo
    | Error of string
    | NotConfigured

/// A game's ListenerSet as read from the cluster (Absent = 404). `listeners` maps
/// each named `status.listeners[]` entry to its own Programmed condition.
[<RequireQualifiedAccess>]
type ListenerSetStatus =
    | Absent
    | Present of accepted: bool * programmed: bool * listeners: Map<string, bool>
