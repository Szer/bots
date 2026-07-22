namespace AlitaBot.Llm

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Text.Json
open System.Threading.Tasks
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

/// Cost lookup for image generation, driven by the same LLM_PRICING bot_setting as
/// LlmPricing but a different shape: flat per-image price by quality tier (gpt-image-1
/// bills by tokens, but Azure doesn't expose a stable per-token USD rate for it the way
/// chat models do, so pricing here is per-image-per-quality instead):
///   {"alita-image":{"per_image_low":0.02,"per_image_medium":0.04,"per_image_high":0.08}}
/// Keyed by the image DEPLOYMENT name (unlike LlmPricing, which matches the response's
/// `model` field) — Azure's images API doesn't consistently echo a `model` field across
/// gpt-image-1/dall-e-3, so the deployment name is the one thing always known here.
module ImagePricing =
    /// (deployment, quality) pairs already warned about — logs at most one Warning each.
    let private warned = ConcurrentDictionary<string, byte>()

    /// Substring match against `deployment`, same convention as LlmPricing.tryCost.
    /// Missing entry (unknown deployment, or no per_image_<quality> field for it) -> None
    /// + a one-time Warning, never an exception.
    let tryCost (logger: ILogger) (pricingJson: string) (deployment: string) (quality: string) : float option =
        try
            use doc = JsonDocument.Parse(pricingJson)
            if doc.RootElement.ValueKind <> JsonValueKind.Object then
                None
            else
                let matched =
                    doc.RootElement.EnumerateObject()
                    |> Seq.tryFind (fun prop -> deployment.Contains(prop.Name, StringComparison.OrdinalIgnoreCase))
                match matched with
                | Some prop ->
                    let field = $"per_image_{quality}"
                    match prop.Value.TryGetProperty field with
                    | true, v when v.ValueKind = JsonValueKind.Number -> Some(v.GetDouble())
                    | _ ->
                        if warned.TryAdd($"{deployment}:{quality}", 0uy) then
                            logger.LogWarning(
                                "No LLM_PRICING '{Field}' entry for image deployment '{Deployment}' — cost not recorded",
                                field, deployment)
                        None
                | None ->
                    if warned.TryAdd(deployment, 0uy) then
                        logger.LogWarning("No LLM_PRICING entry matches image deployment '{Deployment}' — cost not recorded", deployment)
                    None
        with _ ->
            None

/// One image-generation call's span + metrics — same lifecycle shape as LlmCall
/// (create at call start, mark the outcome exactly once, dispose at call end) but
/// costed via ImagePricing (per-image-per-quality) instead of LlmPricing (per-token).
/// `usageRecorder`/`ctx` additively persist an `llm_usage` row (kind="image") on success —
/// see IUsageRecorder; existing OTel metrics below are unchanged.
type ImageCall(deployment: string, quality: string, size: string, pricingJson: string, usageRecorder: IUsageRecorder, ctx: UsageContext, logger: ILogger) =
    let activity = Telemetry.botActivity.StartActivity("llm.image")
    let startedAt = Stopwatch.GetTimestamp()

    let setTag (key: string) (value: obj) =
        if not (isNull activity) then %activity.SetTag(key, value)

    do
        setTag "gen_ai.system" "azure.ai.openai"
        setTag "gen_ai.request.model" deployment
        setTag "image.quality" quality
        setTag "image.size" size

    member _.Succeeded(usage: TokenUsage) =
        if usage.TotalTokens > 0 then
            setTag "gen_ai.usage.input_tokens" usage.PromptTokens
            setTag "gen_ai.usage.output_tokens" usage.CompletionTokens
            LlmMetrics.tokensTotal.Add(int64 usage.PromptTokens, KeyValuePair("direction", box "input"))
            LlmMetrics.tokensTotal.Add(int64 usage.CompletionTokens, KeyValuePair("direction", box "output"))
        let cost = ImagePricing.tryCost logger pricingJson deployment quality
        match cost with
        | Some c ->
            setTag "llm.cost_usd" c
            LlmMetrics.costUsdTotal.Add(c)
        | None -> ()
        let inputTokens, outputTokens =
            if usage.TotalTokens > 0 then Some usage.PromptTokens, Some usage.CompletionTokens else None, None
        fireAndForget logger "llm_usage.record" (fun () ->
            usageRecorder.Record("image", deployment, inputTokens, outputTokens, cost, ctx) :> Task)

    member _.Failed(error: LlmError) =
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

/// One LLM call's span + metrics: create at call start, mark the outcome exactly once
/// (Succeeded/Failed), dispose at call end (records the latency histogram). For streamed
/// calls the scope spans the whole stream, so latency covers first byte to [DONE].
/// `kind` is the `llm_usage.kind` value ("chat" | "stt" | "tts" | "embedding") — distinct
/// from `spanName` since one kind can have both stream/non-stream span variants.
/// `usageRecorder`/`ctx` additively persist an `llm_usage` row on success (fire-and-forget,
/// never blocks the reply) — existing OTel metrics below are unchanged.
type LlmCall(spanName: string, kind: string, deployment: string, stream: bool, pricingJson: string, usageRecorder: IUsageRecorder, ctx: UsageContext, logger: ILogger) =
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
        let model = responseModel |> Option.defaultValue deployment
        let cost =
            match usage with
            | Some u ->
                setTag "gen_ai.usage.input_tokens" u.PromptTokens
                setTag "gen_ai.usage.output_tokens" u.CompletionTokens
                LlmMetrics.tokensTotal.Add(int64 u.PromptTokens, KeyValuePair("direction", box "input"))
                LlmMetrics.tokensTotal.Add(int64 u.CompletionTokens, KeyValuePair("direction", box "output"))
                match LlmPricing.tryCost logger pricingJson model u with
                | Some cost ->
                    setTag "llm.cost_usd" cost
                    LlmMetrics.costUsdTotal.Add(cost)
                    Some cost
                | None -> None
            | None -> None
        // Persisted for every successful call, even ones with no TokenUsage (e.g. STT's
        // wire format carries no usage block) — input/output tokens land as NULL then.
        let inputTokens = usage |> Option.map (fun u -> u.PromptTokens)
        let outputTokens = usage |> Option.map (fun u -> u.CompletionTokens)
        fireAndForget logger "llm_usage.record" (fun () ->
            usageRecorder.Record(kind, model, inputTokens, outputTokens, cost, ctx) :> Task)

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
