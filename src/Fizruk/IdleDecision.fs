namespace Fizruk

/// Pure idle-shutdown policy, deliberately independent of Domain/Config so it's
/// trivial to table-test.
module IdleDecision =

    [<RequireQualifiedAccess>]
    type PlayersCount =
        | Ok of int
        | Error of string
        | NotConfigured

    [<RequireQualifiedAccess>]
    type Activity =
        | Ok of string list
        | Error of string

    type Decision =
        | Keep of reason: string
        | Stop

    /// Decides whether a running game should be scaled to 0. Rules are checked in
    /// order and the first match wins — see each branch's comment for why it's Keep.
    let decide
        (desiredReplicas: int)
        (podReady: bool)
        (podAgeMinutes: int option)
        (idleGraceMinutes: int)
        (players: PlayersCount)
        (activity: Activity)
        : Decision =
        if desiredReplicas = 0 then
            Keep "already stopped"
        elif not podReady then
            Keep "pod not ready"
        else
            match podAgeMinutes with
            | None -> Keep "pod age unknown"
            | Some age when age < idleGraceMinutes -> Keep "within start grace period"
            | Some _ ->
                match players with
                | PlayersCount.Error msg -> Keep $"player probe error: {msg}"
                | PlayersCount.Ok n when n > 0 -> Keep $"{n} player(s) online"
                | PlayersCount.Ok _
                | PlayersCount.NotConfigured ->
                    match activity with
                    | Activity.Error msg -> Keep $"activity log error: {msg}"
                    | Activity.Ok events when not (List.isEmpty events) -> Keep "recent activity"
                    | Activity.Ok _ -> Stop
