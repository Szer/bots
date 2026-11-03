module VahterBanBot.Unit.Tests.LlmLayerTests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging.Abstractions
open BotInfra
open BotInfra.JsonSetup
open VahterBanBot.Utils
open VahterBanBot.Types
open VahterBanBot.LlmTriagePipeline
open Xunit

let private context = { ChatDescription = "\nChat: technical discussion"; UserPrompt = "frozen evidence" }
let private logger = NullLogger.Instance
let private result verdict = { Verdict = verdict; PromptTokens = 1; CompletionTokens = 1; LatencyMs = 1 }
let private layer id classify =
    { Id = id; ModelName = id + "-model"; PromptHash = id + "-prompt"; CacheIdentity = id
      Classify = classify }
let private settings order =
    { fromJson<BotConfiguration> "{}" with
        LlmTriageLayers = order
        LlmChatDescriptions = Dictionary<int64, string>() }

[<Theory>]
[<InlineData("SPAM")>]
[<InlineData("NOT_SPAM")>]
let ``a firm first verdict stops subsequent layers`` verdict = task {
    let first = layer "first" (fun _ -> Task.FromResult(result (LlmVerdict.FromString(verdict, Some "first reason", None))))
    let second = layer "second" (fun _ -> failwith "second layer must not run")
    let! deciding, actual = runLayers logger true [first; second] context CancellationToken.None
    Assert.Equal("first-model", deciding.ModelName)
    Assert.Equal(LlmVerdict.FromString(verdict, Some "first reason", None), actual.Verdict)
}

[<Theory>]
[<InlineData("SKIP")>]
[<InlineData("ERROR")>]
[<InlineData("exception")>]
[<InlineData("timeout")>]
let ``abstentions and failures escalate with identical evidence`` outcome = task {
    let seen = ResizeArray<LlmTriageContext>()
    let first = layer "first" (fun (input, _) ->
        seen.Add input
        if outcome = "exception" then raise (InvalidOperationException "unavailable")
        if outcome = "timeout" then raise (OperationCanceledException "provider timeout")
        Task.FromResult(result (LlmVerdict.FromString(outcome, Some "private first-layer reason", None))))
    let second = layer "second" (fun (input, _) ->
        seen.Add input
        Task.FromResult(result (LlmVerdict.Kill (Some "second reason", None))))
    let! deciding, actual = runLayers logger true [first; second] context CancellationToken.None
    Assert.Equal("second-model", deciding.ModelName)
    Assert.Equal(LlmVerdict.Kill (Some "second reason", None), actual.Verdict)
    Assert.Equal(2, seen.Count)
    Assert.Same(context, seen[0])
    Assert.Same(context, seen[1])
}

[<Theory>]
[<InlineData("SKIP")>]
[<InlineData("ERROR")>]
let ``exhausted layers preserve the last human-review outcome`` verdict = task {
    let first = layer "first" (fun _ -> Task.FromResult(result LlmVerdict.Error))
    let expected = LlmVerdict.FromString(verdict, Some "last reason", None)
    let last = layer "last" (fun _ -> Task.FromResult(result expected))
    let! deciding, actual = runLayers logger true [first; last] context CancellationToken.None
    Assert.Equal("last", deciding.Id)
    Assert.Equal(expected, actual.Verdict)
}

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``content filter policy controls whether another layer is tried`` contentFilterIsSpam = task {
    let first = layer "first" (fun _ -> Task.FromResult(result (LlmVerdict.ContentFiltered "sexual=high")))
    let second = layer "second" (fun _ -> Task.FromResult(result (LlmVerdict.NotSpam (None, None))))
    let! deciding, _ = runLayers logger contentFilterIsSpam [first; second] context CancellationToken.None
    Assert.Equal((if contentFilterIsSpam then "first" else "second"), deciding.Id)
}

[<Fact>]
let ``cancellation stops escalation and cannot yield an automatic verdict`` () = task {
    use cts = new CancellationTokenSource()
    let first = layer "first" (fun (_, ct) ->
        Assert.Equal(cts.Token, ct)
        cts.Cancel()
        Task.FromResult(result (LlmVerdict.Kill (None, None))))
    let second = layer "second" (fun _ -> failwith "cancelled pipeline must not escalate")
    let! _, actual = runLayers logger true [first; second] context cts.Token
    Assert.Equal(LlmVerdict.Error, actual.Verdict)
}

[<Fact>]
let ``configured order selects the first accepting provider`` () = task {
    let visited = ResizeArray<string>()
    let provider id =
        { new ILlmTriageLayer with
            member _.Id = id
            member _.Configure _ = layer id (fun _ ->
                visited.Add id
                Task.FromResult(result (LlmVerdict.NotSpam (None, None)))) }
    let registry = Map ["first", provider "first"; "second", provider "second"]
    for order in [["first"; "second"]; ["second"; "first"]] do
        match configureLayers registry (settings order) with
        | Error error -> failwith error
        | Ok configured ->
            let! deciding, _ = runLayers logger true configured context CancellationToken.None
            Assert.Equal(order.Head, deciding.Id)
    Assert.Equal<string list>(["first"; "second"], List.ofSeq visited)
}

[<Theory>]
[<InlineData("[]")>]
[<InlineData("[\"azure-sol\",\"azure-sol\"]")>]
[<InlineData("[\"\"]")>]
let ``empty or duplicate layer orders are rejected`` json =
    %Assert.Throws<ArgumentException>(fun () -> %(parseLayerOrder json))

[<Fact>]
let ``unknown layers invalidate the entire route`` () =
    let conf = settings ["unknown-layer"]
    match configureLayers Map.empty conf with
    | Error _ -> ()
    | Ok _ -> failwith "An unknown layer must not be silently omitted."

[<Fact>]
let ``cache identities isolate order model prompt gate and policy changes`` () =
    let conf = settings ["first"; "second"]
    let first = layer "first" (fun _ -> Task.FromResult(result LlmVerdict.Error))
    let second = layer "second" first.Classify
    let original = pipelineCacheIdentity conf [first; second]
    Assert.NotEqual<string>(original, pipelineCacheIdentity conf [second; first])
    Assert.NotEqual<string>(original, pipelineCacheIdentity conf [{first with CacheIdentity = "updated-model-prompt-gate"}; second])
    let descriptions = Dictionary<int64, string>()
    descriptions.Add(42L, "different policy")
    Assert.NotEqual<string>(original, pipelineCacheIdentity {conf with LlmChatDescriptions = descriptions} [first; second])

[<Fact>]
let ``disabled content-filter action remains human review after exhaustion`` () = task {
    let provider = layer "only" (fun _ -> Task.FromResult(result (LlmVerdict.ContentFiltered "sexual=high")))
    let! _, actual = runLayers logger false [provider] context CancellationToken.None
    Assert.Equal(LlmVerdict.Error, actual.Verdict)
}
