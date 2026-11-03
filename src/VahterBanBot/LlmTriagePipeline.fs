module VahterBanBot.LlmTriagePipeline

open System
open System.Diagnostics
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open VahterBanBot.Types
open VahterBanBot.Utils
open VahterBanBot.Telemetry
open BotInfra
open BotInfra.JsonSetup

/// Frozen evidence, including nonce fences; layer outputs never enter another layer's input.
type LlmTriageContext =
    { ChatDescription: string
      UserPrompt: string }

type LlmLayerResult =
    { Verdict: LlmVerdict
      PromptTokens: int
      CompletionTokens: int
      LatencyMs: int }

/// One layer bound to a settings snapshot; CacheIdentity covers its model, prompt and acceptance gate.
type ConfiguredLlmLayer =
    { Id: string
      ModelName: string
      PromptHash: string
      CacheIdentity: string
      Classify: LlmTriageContext * CancellationToken -> Task<LlmLayerResult> }

type ILlmTriageLayer =
    abstract member Id: string
    abstract member Configure: BotConfiguration -> ConfiguredLlmLayer

type LlmTriageSource =
    { ModelName: string
      PromptHash: string }

type LlmTriageResult =
    { Verdict: LlmVerdict
      Source: LlmTriageSource option }

let parseLayerOrder (json: string) : string list =
    let order = fromJson<string list> json
    if List.isEmpty order || order |> List.exists String.IsNullOrWhiteSpace then
        invalidArg "LLM_TRIAGE_LAYERS" "Expected a non-empty JSON array of layer IDs."
    if List.distinct order <> order then
        invalidArg "LLM_TRIAGE_LAYERS" "Layer IDs must be unique."
    order

let configureLayers (registry: Map<string, ILlmTriageLayer>) (conf: BotConfiguration) =
    let order = conf.LlmTriageLayers
    match order with
    | [] -> Error "LLM_TRIAGE_LAYERS must contain at least one layer."
    | _ when List.distinct order <> order -> Error "LLM_TRIAGE_LAYERS contains duplicate IDs."
    | _ ->
        match order |> List.tryFind (fun id -> not (registry.ContainsKey id)) with
        | Some id -> Error $"Unknown LLM triage layer: {id}"
        | None -> Ok (order |> List.map (fun id -> registry[id].Configure conf))

let pipelineCacheIdentity (conf: BotConfiguration) (layers: ConfiguredLlmLayer list) =
    let policies = conf.LlmChatDescriptions |> Seq.map (fun kv -> kv.Key, kv.Value) |> Seq.sortBy fst |> Seq.toList
    let identity =
        toJson ("triage-context-v1", layers |> List.map (fun layer -> layer.Id, layer.CacheIdentity), policies, conf.LlmContentFilterIsSpam)
    SHA256.HashData(Encoding.UTF8.GetBytes identity) |> Convert.ToHexString

let runLayers (logger: ILogger) contentFilterIsSpam (layers: ConfiguredLlmLayer list) context (ct: CancellationToken) =
    let rec run remaining = task {
        match remaining with
        | [] -> return invalidArg "layers" "At least one configured layer is required."
        | layer :: tail ->
            use activity = botActivity.StartActivity("llmTriage.route")
            let sw = Stopwatch.StartNew()
            let! result = task {
                try
                    ct.ThrowIfCancellationRequested()
                    return! layer.Classify(context, ct)
                with ex ->
                    logger.LogWarning(ex, "LLM layer {Layer} failed", layer.Id)
                    return { Verdict = LlmVerdict.Error; PromptTokens = 0; CompletionTokens = 0; LatencyMs = int sw.ElapsedMilliseconds }
            }
            let accepted =
                match result.Verdict with
                | LlmVerdict.Kill _ | LlmVerdict.NotSpam _ -> true
                | LlmVerdict.ContentFiltered _ -> contentFilterIsSpam
                | LlmVerdict.Skip _ | LlmVerdict.Error -> false
            if not (isNull activity) then
                %activity.SetTag("llm.layer", layer.Id).SetTag("llm.model", layer.ModelName)
                    .SetTag("llm.accepted", accepted).SetTag("latency_ms", sw.ElapsedMilliseconds)
                    .SetTag("llm.prompt_hash", layer.PromptHash)
                    .SetTag("llm.input_tokens", result.PromptTokens).SetTag("llm.output_tokens", result.CompletionTokens)
            logger.LogInformation("LLM layer {Layer} model={Model} verdict={Verdict} accepted={Accepted} latencyMs={LatencyMs}",
                                  layer.Id, layer.ModelName, result.Verdict, accepted, sw.ElapsedMilliseconds)
            match accepted, tail, ct.IsCancellationRequested with
            | _, _, true -> return layer, { result with Verdict = LlmVerdict.Error }
            | true, _, _ -> return layer, result
            | false, [], false ->
                match result.Verdict with
                | LlmVerdict.ContentFiltered _ -> return layer, { result with Verdict = LlmVerdict.Error }
                | _ -> return layer, result
            | false, _, false -> return! run tail
    }
    run layers
