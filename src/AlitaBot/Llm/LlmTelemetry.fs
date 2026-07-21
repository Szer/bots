namespace AlitaBot.Llm

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Text.Json
open Microsoft.Extensions.Logging
open AlitaBot
open BotInfra

module LlmMetrics =
    /// Token throughput, tagged by `direction` ∈ {input, output}.
    let tokensTotal = Metrics.meter.CreateCounter<int64>("alitabot_llm_tokens_total")
    let costUsdTotal = Metrics.meter.CreateCounter<float>("alitabot_llm_cost_usd_total")
    let latencyMs = Metrics.meter.CreateHistogram<float>("alitabot_llm_latency_ms")

/// Cost lookup driven by the LLM_PRICING bot_setting (hot-reloadable JSON):
/// {"gpt-5-mini":{"input_per_1m":0.25,"output_per_1m":2.00}}
module LlmPricing =
    type ModelPricing = { InputPer1M: float; OutputPer1M: float }

    /// Models already warned about — an unknown model logs exactly one Warning.
    let private warnedModels = ConcurrentDictionary<string, byte>()

    /// Lenient parse: malformed JSON or entries with missing/non-numeric prices are skipped.
    let parse (json: string) : (string * ModelPricing) list =
        try
            use doc = JsonDocument.Parse(json)
            if doc.RootElement.ValueKind <> JsonValueKind.Object then []
            else
                [ for prop in doc.RootElement.EnumerateObject() do
                    if prop.Value.ValueKind = JsonValueKind.Object then
                        let price (name: string) =
                            match prop.Value.TryGetProperty name with
                            | true, v when v.ValueKind = JsonValueKind.Number -> Some(v.GetDouble())
                            | _ -> None
                        match price "input_per_1m", price "output_per_1m" with
                        | Some input, Some output -> prop.Name, { InputPer1M = input; OutputPer1M = output }
                        | _ -> () ]
        with _ -> []

    /// Pricing keys match by substring so both the response model ("gpt-5-mini-2025-08-07")
    /// and the deployment name ("alita-gpt-5-mini") resolve to the "gpt-5-mini" entry.
    let tryCost (logger: ILogger) (pricingJson: string) (modelName: string) (usage: TokenUsage) : float option =
        let matched =
            parse pricingJson
            |> List.tryFind (fun (key, _) -> modelName.Contains(key, StringComparison.OrdinalIgnoreCase))
        match matched with
        | Some(_, p) ->
            Some(
                float usage.PromptTokens / 1_000_000.0 * p.InputPer1M
                + float usage.CompletionTokens / 1_000_000.0 * p.OutputPer1M
            )
        | None ->
            if warnedModels.TryAdd(modelName, 0uy) then
                logger.LogWarning("No LLM_PRICING entry matches model '{Model}' — cost not recorded", modelName)
            None

/// One LLM call's span + metrics: create at call start, mark the outcome exactly once
/// (Succeeded/Failed), dispose at call end (records the latency histogram). For streamed
/// calls the scope spans the whole stream, so latency covers first byte to [DONE].
type LlmCall(spanName: string, deployment: string, stream: bool, pricingJson: string, logger: ILogger) =
    let activity = Telemetry.botActivity.StartActivity(spanName)
    let startedAt = Stopwatch.GetTimestamp()

    let setTag (key: string) (value: obj) =
        if not (isNull activity) then %activity.SetTag(key, value)

    do
        setTag "gen_ai.system" "azure.ai.openai"
        setTag "gen_ai.request.model" deployment
        setTag "llm.stream" stream

    member _.Succeeded(responseModel: string option, usage: TokenUsage option, retries: int) =
        setTag "llm.retries" retries
        match usage with
        | Some u ->
            setTag "gen_ai.usage.input_tokens" u.PromptTokens
            setTag "gen_ai.usage.output_tokens" u.CompletionTokens
            LlmMetrics.tokensTotal.Add(int64 u.PromptTokens, KeyValuePair("direction", box "input"))
            LlmMetrics.tokensTotal.Add(int64 u.CompletionTokens, KeyValuePair("direction", box "output"))
            let model = responseModel |> Option.defaultValue deployment
            match LlmPricing.tryCost logger pricingJson model u with
            | Some cost ->
                setTag "llm.cost_usd" cost
                LlmMetrics.costUsdTotal.Add(cost)
            | None -> ()
        | None -> ()

    member _.Failed(error: LlmError, retries: int) =
        setTag "llm.retries" retries
        let errorType =
            match error with
            | LlmError.RateLimited _ -> "rate_limited"
            | LlmError.ContentFiltered _ -> "content_filtered"
            | LlmError.ApiError _ -> "api_error"
            | LlmError.NetworkError _ -> "network_error"
        setTag "error.type" errorType
        if not (isNull activity) then %activity.SetStatus(ActivityStatusCode.Error, errorType)

    interface IDisposable with
        member _.Dispose() =
            LlmMetrics.latencyMs.Record(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds)
            if not (isNull activity) then activity.Dispose()
