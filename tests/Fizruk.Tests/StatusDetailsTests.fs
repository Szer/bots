module Fizruk.Tests.StatusDetailsTests

open Fizruk
open Xunit

[<Theory>]
[<InlineData("steel-processing", 1, "Steel processing")>]
[<InlineData("automation-2", 2, "Automation 2")>]
[<InlineData("mining-productivity-3", 12, "Mining productivity 12")>]
let ``displayName humanizes the technology name and shows the level`` (name: string, level: int, expected: string) =
    Assert.Equal(expected, FactorioResearch.displayName name level)

[<Fact>]
let ``parse reads name, level and progress`` () =
    let result = FactorioResearch.parse "promethium-science-pack\t1\t0.3745\n"
    Assert.Equal(Ok(Some ({ Name = "Promethium science pack"; Progress = 0.3745 }: FactorioResearch.Research)), result)

[<Fact>]
let ``parse treats "none" as no research`` () =
    Assert.Equal(Ok None, FactorioResearch.parse "none\n")

[<Fact>]
let ``parse treats empty output as an error`` () =
    Assert.Equal(Error "empty response", FactorioResearch.parse "")

[<Fact>]
let ``parse rejects unexpected output`` () =
    match FactorioResearch.parse "Cannot execute command" with
    | Error _ -> ()
    | Ok r -> Assert.Fail $"expected Error, got {r}"

[<Fact>]
let ``format shows progress with one decimal`` () =
    let line = FactorioResearch.format (Ok(Some ({ Name = "Railgun damage 3"; Progress = 0.3745 }: FactorioResearch.Research)))
    Assert.Equal("Research: Railgun damage 3 (37.5%).", line)

[<Fact>]
let ``format reports idle labs and errors`` () =
    Assert.Equal("Research: nothing queued.", FactorioResearch.format (Ok None))
    Assert.Equal("Research: unknown (RCON request timed out).", FactorioResearch.format (Error "RCON request timed out"))

[<Fact>]
let ``command is silent so players' consoles don't echo it`` () =
    Assert.StartsWith("/sc ", FactorioResearch.command)
