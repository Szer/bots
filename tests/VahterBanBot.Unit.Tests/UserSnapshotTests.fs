module VahterBanBot.Unit.Tests.UserSnapshotTests

open System
open System.Text.Json
open BotInfra
open VahterBanBot.Types
open Xunit

let private t0 = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
let private userId = 42L

/// Exercises every UserEvent case, every Actor case and the legacy `bannedBy`-only ban shape.
let private canonicalEvents : UserEvent list =
    let ban (actor: Actor option) (bannedBy: BannedBy option) (at: DateTime) =
        UserBanned {| userId = userId; bannedBy = bannedBy; actor = actor; chatId = Some -100L
                      messageId = Some 7L; messageText = Some "spam"; bannedAt = at |}
    [ UsernameChanged {| userId = userId; username = Some "alice" |}
      UserReactionRecorded {| userId = userId; chatId = Some -100L; messageId = Some 5L; emoji = Some "🔥"; delta = 2 |}
      UserReactionRecorded {| userId = userId; chatId = None; messageId = None; emoji = None; delta = -1 |}
      ban None (Some (BannedByVahter {| vahterId = 9L; vahterUsername = Some "v"; chatId = -100L; messageId = 7; messageText = None |})) t0
      UserUnbanned {| userId = userId; unbannedBy = Some 9L; actor = None |}
      ban None (Some (BannedByAI {| chatId = -100L; messageId = 8; messageText = None; modelName = "m"; promptHash = "h" |}))
          (t0.AddDays 1.0)
      UserUnbanned {| userId = userId; unbannedBy = None; actor = Some Actor.ML |}
      ReactionTriageNotSpamSet {| userId = userId; until = t0.AddDays 2.0; actor = Actor.LLM {| modelName = "m"; promptHash = "h" |} |}
      SpamProtectionGranted {| userId = userId; until = t0.AddDays 3.0; chatId = -100L; messageId = 11L; vahterId = 9L |}
      SpamProtectionConsumed {| userId = userId; chatId = -100L; messageId = 12L |}
      SpamProtectionRevoked {| userId = userId; reason = "budget" |}
      SpamProtectionGranted {| userId = userId; until = t0.AddDays 4.0; chatId = -100L; messageId = 13L; vahterId = 9L |}
      SpamProtectionConsumed {| userId = userId; chatId = -100L; messageId = 14L |}
      ban (Some (Actor.Bot (Some {| botUserId = 1L; botUsername = "vahter" |}))) None (t0.AddDays 5.0)
      UsernameChanged {| userId = userId; username = None |}
      ban (Some (Actor.User {| userId = 9L; username = Some "v" |})) None (t0.AddDays 6.0) ]

let private foldAll (seed: User) (events: UserEvent list) = events |> List.fold (fun s e -> User.Fold(s, e)) seed

let private roundTrip (u: User) =
    JsonSerializer.Deserialize<User>(JsonSerializer.Serialize(u, eventJsonOpts), eventJsonOpts)

// Append a new (SchemaVersion, shape) pair whenever User changes; never edit an existing pair.
let private pinnedShapes =
    [ 1, "User{Id: System.Int64; Banned: option<(Actor[User of (Item: {userId: System.Int64; username: option<System.String>}) | Bot of (Item: option<{botUserId: System.Int64; botUsername: System.String}>) | ML | LLM of (Item: {modelName: System.String; promptHash: System.String})] * System.DateTime)>; Username: option<System.String>; ReactionCount: System.Int32; NotSpamUntil: option<System.DateTime>; SpamProtectionUntil: option<System.DateTime>; SpamProtectionHits: System.Int32}" ]

// Append a new (SchemaVersion, folded state) pair whenever Fold's behaviour changes.
let private pinnedFolds =
    [ 1, { Id = userId
           Banned = Some (Actor.User {| userId = 9L; username = Some "v" |}, t0.AddDays 6.0)
           Username = None
           ReactionCount = 1
           NotSpamUntil = Some (t0.AddDays 2.0)
           SpamProtectionUntil = Some (t0.AddDays 4.0)
           SpamProtectionHits = 1 } ]

[<Fact>]
let ``User shape is pinned to the current snapshot SchemaVersion`` () =
    let version, shape = List.last pinnedShapes
    let actual = SnapshotShape.describe typeof<User>
    Assert.True(
        (shape = actual && version = User.SnapshotPolicy.SchemaVersion),
        $"User's shape no longer matches pin v{version} (policy is v{User.SnapshotPolicy.SchemaVersion}). "
        + "Bump User.SnapshotPolicy.SchemaVersion and append a pin with the new shape:\n" + actual)

[<Fact>]
let ``User.Fold behaviour is pinned to the current snapshot SchemaVersion`` () =
    let version, expected = List.last pinnedFolds
    let actual = foldAll User.Zero canonicalEvents
    Assert.True(
        (expected = actual && version = User.SnapshotPolicy.SchemaVersion),
        $"User.Fold no longer produces pin v{version} (policy is v{User.SnapshotPolicy.SchemaVersion}). "
        + $"Bump User.SnapshotPolicy.SchemaVersion and append a pin with the new state:\n%A{actual}")

[<Fact>]
let ``pins use strictly increasing schema versions`` () =
    for pins in [ List.map fst pinnedShapes; List.map fst pinnedFolds ] do
        Assert.Equal<int list>(List.sort (List.distinct pins), pins)

[<Fact>]
let ``every intermediate User state survives the snapshot JSON round-trip`` () =
    for k in 0 .. canonicalEvents.Length do
        let state = foldAll User.Zero (List.take k canonicalEvents)
        Assert.Equal(state, roundTrip state)

[<Fact>]
let ``snapshot plus tail equals full replay at every split point`` () =
    let full = foldAll User.Zero canonicalEvents
    for k in 0 .. canonicalEvents.Length do
        let snapshot = roundTrip (foldAll User.Zero (List.take k canonicalEvents))
        Assert.Equal(full, foldAll snapshot (List.skip k canonicalEvents))
