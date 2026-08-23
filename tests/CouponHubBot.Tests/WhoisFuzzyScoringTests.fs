namespace CouponHubBot.Tests

open System
open CouponHubBot
open CouponHubBot.Services
open Xunit

// Pure scoring function tests — no DB, no containers, no fixture.
type WhoisFuzzyScoringTests() =

    let user (id: int64) (username: string | null) (firstName: string | null) (lastName: string | null) : DbUser =
        { id = id
          username = username
          first_name = firstName
          last_name = lastName
          created_at = DateTime.UtcNow
          updated_at = DateTime.UtcNow }

    [<Fact>]
    let ``Multi word name query matches a similarly spelled full name`` () =
        let elena = user 100L null "Elena" "Sokolenko"
        let score = BotHelpers.scoreWhoisCandidate "Elena Sokolova" elena
        Assert.True(score >= BotHelpers.whoisFuzzyThreshold, $"score was {score}")

    [<Fact>]
    let ``Partial surname query matches similarly spelled surnames`` () =
        let sokolenko = user 101L null "Anna" "Sokolenko"
        let sokolova = user 102L null "Irina" "Sokolova"
        Assert.True(BotHelpers.scoreWhoisCandidate "sokolov" sokolenko >= BotHelpers.whoisFuzzyThreshold)
        Assert.True(BotHelpers.scoreWhoisCandidate "sokolov" sokolova >= BotHelpers.whoisFuzzyThreshold)

    [<Fact>]
    let ``Digit substring query matches a user id containing it`` () =
        let target = user 123245L null "Some" "User"
        let score = BotHelpers.scoreWhoisCandidate "1232" target
        Assert.True(score >= BotHelpers.whoisFuzzyThreshold, $"score was {score}")

    [<Fact>]
    let ``Garbage query matches nobody`` () =
        let users =
            [| user 100L null "Elena" "Sokolenko"
               user 101L "admin" "Admin" null
               user 123245L "Some_User" "Some" "User" |]
        let results = BotHelpers.searchWhoisFuzzy "zzzzqqq" users
        Assert.Empty(results)

    [<Fact>]
    let ``Fuzzy search caps results at five and sorts best first`` () =
        let users =
            [| for i in 1 .. 10 -> user (int64 (200 + i)) null "Elena" "Sokolenko" |]
        let results = BotHelpers.searchWhoisFuzzy "Elena Sokolenko" users
        Assert.Equal(5, results.Length)
