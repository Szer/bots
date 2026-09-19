module Fizruk.Tests.CommandParsingTests

open Fizruk
open Fizruk.Tests.Fakes
open Xunit

[<Fact>]
let ``plain text is not a command`` () =
    Assert.Equal(None, Commands.tryParse None "hello there")

[<Fact>]
let ``bare start command with no game arg`` () =
    let parsed = Commands.tryParse None "/start"
    Assert.Equal(Some { Action = BotAction.Start; GameArg = None }, parsed)

[<Fact>]
let ``start command with a game arg`` () =
    let parsed = Commands.tryParse None "/start factorio"
    Assert.Equal(Some { Action = BotAction.Start; GameArg = Some "factorio" }, parsed)

[<Fact>]
let ``command matching is case-insensitive`` () =
    let parsed = Commands.tryParse None "/STOP"
    Assert.Equal(Some { Action = BotAction.Stop; GameArg = None }, parsed)

[<Fact>]
let ``suffix addressed to this bot is stripped, case-insensitively`` () =
    let parsed = Commands.tryParse (Some "fizruk_bot") "/Status@Fizruk_Bot factorio"
    Assert.Equal(Some { Action = BotAction.Status; GameArg = Some "factorio" }, parsed)

[<Fact>]
let ``suffix addressed to a different bot is ignored`` () =
    Assert.Equal(None, Commands.tryParse (Some "fizruk_bot") "/start@some_other_bot")

[<Fact>]
let ``unrecognized command word is ignored`` () =
    Assert.Equal(None, Commands.tryParse None "/help")

[<Fact>]
let ``unauthorized chat yields Unauthorized`` () =
    let config = sampleConfig ()
    Assert.Equal(Commands.Resolution.Unauthorized, Commands.resolveGame config.Chats BotAction.Status -999L None)

[<Fact>]
let ``single-game chat resolves without naming the game`` () =
    let config = sampleConfig ()
    Assert.Equal(Commands.Resolution.Resolved "factorio", Commands.resolveGame config.Chats BotAction.Start -100L None)

[<Fact>]
let ``single-game chat shortcut also applies to status`` () =
    let config = sampleConfig ()
    Assert.Equal(Commands.Resolution.Resolved "factorio", Commands.resolveGame config.Chats BotAction.Status -100L None)

[<Fact>]
let ``explicit game name outside the chat's ACL is Unknown`` () =
    let config = sampleConfig ()
    Assert.Equal(Commands.Resolution.Unknown [ "factorio" ], Commands.resolveGame config.Chats BotAction.Start -100L (Some "minecraft-creative"))

[<Fact>]
let ``explicit game name inside the chat's ACL resolves`` () =
    let config = sampleConfig ()
    Assert.Equal(
        Commands.Resolution.Resolved "minecraft-creative",
        Commands.resolveGame config.Chats BotAction.Start -200L (Some "minecraft-creative"))

[<Fact>]
let ``multi-game chat with no name is ambiguous for start and stop`` () =
    let config = sampleConfig ()
    let expected = Commands.Resolution.Ambiguous [ "factorio"; "minecraft-creative" ]
    Assert.Equal(expected, Commands.resolveGame config.Chats BotAction.Start -200L None)
    Assert.Equal(expected, Commands.resolveGame config.Chats BotAction.Stop -200L None)

[<Fact>]
let ``multi-game chat with no name reports every game for status`` () =
    let config = sampleConfig ()
    Assert.Equal(
        Commands.Resolution.AllGames [ "factorio"; "minecraft-creative" ],
        Commands.resolveGame config.Chats BotAction.Status -200L None)

[<Fact>]
let ``explicit game name resolves case-insensitively to the configured spelling`` () =
    let config = sampleConfig ()
    Assert.Equal(
        Commands.Resolution.Resolved "minecraft-creative",
        Commands.resolveGame config.Chats BotAction.Start -200L (Some "Minecraft-Creative"))
    Assert.Equal(
        Commands.Resolution.Resolved "minecraft-creative",
        Commands.resolveGame config.Chats BotAction.Start -200L (Some "MINECRAFT-CREATIVE"))
