module Fizruk.Tests.IdleDecisionTests

open Fizruk.IdleDecision
open Xunit

let private isStop decision =
    match decision with
    | Stop -> true
    | Keep _ -> false

[<Fact>]
let ``already stopped is always kept`` () =
    Assert.False(isStop (decide 0 false None 45 (PlayersCount.Ok 0) (Activity.Ok [])))

[<Fact>]
let ``pod not ready is kept`` () =
    Assert.False(isStop (decide 1 false (Some 999) 45 (PlayersCount.Ok 0) (Activity.Ok [])))

[<Fact>]
let ``unknown pod age is kept`` () =
    Assert.False(isStop (decide 1 true None 45 (PlayersCount.Ok 0) (Activity.Ok [])))

[<Fact>]
let ``pod younger than the grace period is kept`` () =
    Assert.False(isStop (decide 1 true (Some 10) 45 (PlayersCount.Ok 0) (Activity.Ok [])))

[<Fact>]
let ``player probe error is kept`` () =
    Assert.False(isStop (decide 1 true (Some 999) 45 (PlayersCount.Error "timeout") (Activity.Ok [])))

[<Fact>]
let ``players online is kept`` () =
    Assert.False(isStop (decide 1 true (Some 999) 45 (PlayersCount.Ok 2) (Activity.Ok [])))

[<Fact>]
let ``activity log error is kept`` () =
    Assert.False(isStop (decide 1 true (Some 999) 45 (PlayersCount.Ok 0) (Activity.Error "log unavailable")))

[<Fact>]
let ``recent activity is kept even with zero players`` () =
    Assert.False(isStop (decide 1 true (Some 999) 45 (PlayersCount.Ok 0) (Activity.Ok [ "[JOIN] bob" ])))

[<Fact>]
let ``no players, no activity, past grace period stops`` () =
    Assert.True(isStop (decide 1 true (Some 999) 45 (PlayersCount.Ok 0) (Activity.Ok [])))

[<Fact>]
let ``NotConfigured players with no activity past grace period stops`` () =
    Assert.True(isStop (decide 1 true (Some 999) 45 PlayersCount.NotConfigured (Activity.Ok [])))

[<Fact>]
let ``pod age exactly at the grace boundary is eligible to stop`` () =
    Assert.True(isStop (decide 1 true (Some 45) 45 (PlayersCount.Ok 0) (Activity.Ok [])))
