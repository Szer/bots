namespace SerializationCompat.Tests

open System
open System.IO
open System.Reflection
open System.Text.Json
open Xunit

/// Shared helpers: fixture loading + the two JSON stacks under test.
module Fixtures =

    /// Funogram's composed JsonSerializerOptions (snake_case + DU/unix-date/option
    /// converters). Internal in Funogram 3.0.4, so we grab it via reflection once.
    /// Using the exact same instance the library serializes with guarantees these
    /// tests can never drift from Funogram's real behavior. If a future Funogram
    /// exposes it publicly, swap this for the public accessor.
    let funogramOptions =
        let prop =
            Assembly.Load("Funogram")
                .GetType("Funogram.Tools")
                .GetProperty("options", BindingFlags.Static ||| BindingFlags.NonPublic ||| BindingFlags.Public)
        prop.GetValue(null) :?> JsonSerializerOptions

    /// Telegram.Bot's STJ options as configured by the bots today (JsonBotAPI + hack).
    let telegramBotOptions = BotInfra.TelegramHelpers.telegramJsonOptions

    let private fixturesRoot =
        Path.Combine(AppContext.BaseDirectory, "fixtures")

    let private loadDir sub =
        Directory.GetFiles(Path.Combine(fixturesRoot, sub), "*.json")
        |> Array.map (fun f -> Path.GetFileNameWithoutExtension f, File.ReadAllText f)
        |> Array.sortBy fst

    /// All message fixtures except legacy-object-form: prod's backfilled object-form
    /// rawMessage rows are all literally {} (no fields), so that fixture gets its own
    /// dedicated tolerance test instead of the field-level theories.
    let messageFixtures () =
        loadDir "messages" |> Array.filter (fun (n, _) -> n <> "legacy-object-form")

    let legacyObjectFormFixture () =
        loadDir "messages" |> Array.find (fun (n, _) -> n = "legacy-object-form") |> snd
    let callbackFixtures () = loadDir "callbacks"

    let messageFixture name =
        messageFixtures () |> Array.find (fun (n, _) -> n = name) |> snd

    /// xunit MemberData source: fixture names only (content re-read in the test
    /// so failures name the offending file).
    let messageFixtureNames () : TheoryData<string> =
        let data = TheoryData<string>()
        for name, _ in messageFixtures () do data.Add name
        data

type FixtureSanityTests() =
    [<Fact>]
    member _.``message fixtures are present`` () =
        Assert.NotEmpty(Fixtures.messageFixtures ())

    [<Fact>]
    member _.``callback fixtures are present`` () =
        Assert.NotEmpty(Fixtures.callbackFixtures ())
