module BotInfra.Tests.SnapshotShapeTests

open System
open BotInfra
open Xunit

type Kind =
    | Plain
    | Tagged of {| tag: string; weight: int option |}

type Node =
    { Label: string
      Children: Node list }

type Sample =
    { Id: int64
      Kind: Kind
      Seen: (Kind * DateTime) option
      Tree: Node }

[<Fact>]
let ``describe renders nested records, unions, anonymous records, options and tuples`` () =
    let expected =
        "Sample{Id: System.Int64; "
        + "Kind: Kind[Plain | Tagged of (Item: {tag: System.String; weight: option<System.Int32>})]; "
        + "Seen: option<(Kind[Plain | Tagged of (Item: {tag: System.String; weight: option<System.Int32>})] * System.DateTime)>; "
        + "Tree: Node{Label: System.String; Children: list<@Node>}}"
    Assert.Equal(expected, SnapshotShape.describe typeof<Sample>)

type SampleWithExtraField =
    { Id: int64
      Kind: Kind
      Seen: (Kind * DateTime) option
      Tree: Node
      Extra: int option }

[<Fact>]
let ``describe changes when an optional field is added`` () =
    let a = SnapshotShape.describe typeof<Sample>
    let b = SnapshotShape.describe typeof<SampleWithExtraField>
    Assert.NotEqual<string>(a.Replace("SampleWithExtraField", "Sample"), b.Replace("SampleWithExtraField", "Sample"))
