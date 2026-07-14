namespace SerializationCompat.Tests

open System.Text
open System.Text.Json
open Xunit
open SerializationCompat.Tests.Fixtures

/// Proves the persisted Telegram wire JSON (written by Telegram.Bot-based code,
/// stored in event.data->'rawMessage' — as a JSON string in live rows, see issue
/// #166; these fixtures are the unwrapped message objects) survives the Funogram
/// migration:
///   1. old JSON parses into Funogram types (new binary reads old rows);
///   2. Funogram re-serialization keeps the JSONB shape the MlData SQL reads
///      (entities[*].type, message_id, date as unix seconds);
///   3. Funogram output parses back into Telegram.Bot types (rollback safety).
type MessageCompatTests() =

    static member MessageFixtureNames : TheoryData<string> = messageFixtureNames ()

    member private _.EntityTypes (root: JsonElement) =
        match root.TryGetProperty "entities" with
        | true, ents -> [ for e in ents.EnumerateArray() -> e.GetProperty("type").GetString() ]
        | false, _ -> []

    [<Theory; MemberData(nameof MessageCompatTests.MessageFixtureNames)>]
    member this.``old wire JSON parses into Funogram Message`` (name: string) =
        let json = messageFixture name
        let original = JsonDocument.Parse(json).RootElement

        let msg = JsonSerializer.Deserialize<Funogram.Telegram.Types.Message>(json, funogramOptions)

        Assert.Equal(original.GetProperty("message_id").GetInt64(), msg.MessageId)
        Assert.Equal(original.GetProperty("chat").GetProperty("id").GetInt64(), msg.Chat.Id)
        match original.TryGetProperty "text" with
        | true, t -> Assert.Equal(Some (t.GetString()), msg.Text)
        | false, _ -> ()
        // entity types survive with their exact wire strings
        let expectedEntities = this.EntityTypes original
        let actualEntities =
            msg.Entities |> Option.map (Array.map (fun e -> e.Type) >> Array.toList) |> Option.defaultValue []
        Assert.Equal<string list>(expectedEntities, actualEntities)

    [<Theory; MemberData(nameof MessageCompatTests.MessageFixtureNames)>]
    member this.``Funogram round-trip preserves the JSONB shape the SQL reads`` (name: string) =
        let json = messageFixture name
        let original = JsonDocument.Parse(json).RootElement

        let msg = JsonSerializer.Deserialize<Funogram.Telegram.Types.Message>(json, funogramOptions)
        let reserialized = Encoding.UTF8.GetString(Funogram.Tools.toJson msg)
        let round = JsonDocument.Parse(reserialized).RootElement

        Assert.Equal(original.GetProperty("message_id").GetInt64(), round.GetProperty("message_id").GetInt64())
        // date must stay unix seconds, byte-identical (UnixTimestampDateTimeConverter)
        Assert.Equal(original.GetProperty("date").GetInt64(), round.GetProperty("date").GetInt64())
        Assert.Equal(original.GetProperty("chat").GetProperty("id").GetInt64(),
                     round.GetProperty("chat").GetProperty("id").GetInt64())
        // the exact predicate DB.fs MlData uses: ent->>'type' = 'custom_emoji'
        Assert.Equal<string list>(this.EntityTypes original, this.EntityTypes round)

    [<Fact>]
    member _.``custom_emoji fixture keeps satisfying the MlData SQL predicate after Funogram round-trip`` () =
        let json = messageFixture "custom-emoji"
        let msg = JsonSerializer.Deserialize<Funogram.Telegram.Types.Message>(json, funogramOptions)
        let round = JsonDocument.Parse(Funogram.Tools.toJson msg).RootElement

        let customEmojiCount =
            round.GetProperty("entities").EnumerateArray()
            |> Seq.filter (fun e -> e.GetProperty("type").GetString() = "custom_emoji")
            |> Seq.length
        Assert.True(customEmojiCount > 0, "custom_emoji entity lost in Funogram round-trip — MlData SQL would silently return 0")

    [<Theory; MemberData(nameof MessageCompatTests.MessageFixtureNames)>]
    member this.``rollback safety: Funogram output parses back into Telegram.Bot Message`` (name: string) =
        let json = messageFixture name
        let original = JsonDocument.Parse(json).RootElement

        let funMsg = JsonSerializer.Deserialize<Funogram.Telegram.Types.Message>(json, funogramOptions)
        let funJson = Encoding.UTF8.GetString(Funogram.Tools.toJson funMsg)

        let tbMsg = JsonSerializer.Deserialize<Telegram.Bot.Types.Message>(funJson, telegramBotOptions)

        Assert.Equal(original.GetProperty("message_id").GetInt64(), int64 tbMsg.MessageId)
        Assert.Equal(original.GetProperty("chat").GetProperty("id").GetInt64(), tbMsg.Chat.Id)
        let tbEntityCount =
            let ents = if isNull tbMsg.Entities then [||] else tbMsg.Entities
            ents.Length
        Assert.Equal(List.length (this.EntityTypes original), tbEntityCount)

    [<Fact>]
    member _.``legacy backfilled object-form rawMessage is an empty object and both stacks tolerate it`` () =
        // Prod's object-form rawMessage rows (issue #166 backfill) are all literally {}.
        // Nothing folds or reads fields from them; the only obligation is not to throw.
        let json = legacyObjectFormFixture ()
        let funMsg = JsonSerializer.Deserialize<Funogram.Telegram.Types.Message>(json, funogramOptions)
        Assert.Equal(0L, funMsg.MessageId)
        let tbMsg = JsonSerializer.Deserialize<Telegram.Bot.Types.Message>(json, telegramBotOptions)
        Assert.Equal(0L, int64 tbMsg.MessageId)

    [<Theory; MemberData(nameof MessageCompatTests.MessageFixtureNames)>]
    member _.``both stacks agree on the parsed message`` (name: string) =
        let json = messageFixture name
        let funMsg = JsonSerializer.Deserialize<Funogram.Telegram.Types.Message>(json, funogramOptions)
        let tbMsg = JsonSerializer.Deserialize<Telegram.Bot.Types.Message>(json, telegramBotOptions)

        Assert.Equal(int64 tbMsg.MessageId, funMsg.MessageId)
        Assert.Equal(tbMsg.Chat.Id, funMsg.Chat.Id)
        Assert.Equal(Option.ofObj tbMsg.Text, funMsg.Text)
        match funMsg.From, tbMsg.From with
        | Some f, tb when not (isNull tb) -> Assert.Equal(tb.Id, f.Id)
        | None, tb -> Assert.True(isNull tb)
        | Some _, _ -> Assert.Fail "Funogram parsed a From that Telegram.Bot did not"
