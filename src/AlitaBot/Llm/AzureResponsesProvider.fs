namespace AlitaBot.Llm

open System
open System.Collections.Generic
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open AlitaBot
open BotInfra

/// Azure Responses API wire format (server-managed `web_search` tool, S10 NL tool-calling
/// slice) — a DIFFERENT API surface from AzureWire's chat-completions (module doc comment,
/// GeminiProvider.fs convention): request/response shape, and even the base host, differ.
///
/// DISCOVERY (2026-07-22, curl against the real API — probe run before writing this file,
/// per the plan's binding OQ5): the UNIFIED endpoint `POST
/// https://szer-foundry.openai.azure.com/openai/v1/responses` (NOTE: NOT
/// `AZURE_FOUNDRY_ENDPOINT`'s `https://szer-foundry.cognitiveservices.azure.com` — a
/// DIFFERENT host on the same Azure AI Foundry resource) worked on the FIRST attempt with
/// candidate model `alita-gpt-5-mini` — no `?api-version=` query string needed, no fallback
/// to a legacy versioned form required. Auth: the same `api-key` header AzureWire's chat-
/// completions endpoint uses (not a Bearer token). Model: the `alita-gpt-5-mini` deployment
/// name (the same one LLM_DEPLOYMENT already points at) worked directly as the `model`
/// field — no separate model/deployment split needed for Responses+web_search. Request
/// body: `{"model":"alita-gpt-5-mini","tools":[{"type":"web_search"}],"input":"<query>"}`.
///
/// Response shape: a top-level `output` array mixing `type="reasoning"` (opaque, ignored
/// here), `type="web_search_call"` (opaque, ignored — the model's own internal search
/// actions/queries), and exactly one `type="message"` item whose `content[]` holds
/// `type="output_text"` parts with a `text` string and an `annotations[]` array of
/// `type="url_citation"` objects (`title`, `url`, plus char offsets into `text` — the
/// offsets are ignored here, only title/url are surfaced). `usage` uses the Responses API's
/// OWN field names — `input_tokens`/`output_tokens`/`total_tokens` (NOT chat completions'
/// `prompt_tokens`/`completion_tokens`, the same divergence gpt-image-1's usage block has
/// vs. chat — see AzureWire.parseImageUsage) — mapped onto the same `TokenUsage` shape as
/// everywhere else.
///
/// Surprise: for the probe query ("когда вышел .NET 10?") the model ran a 3-round internal
/// search (two `web_search_call` items plus interleaved `reasoning` items) before producing
/// its one `message` answer — all invisible to the parser below, which only ever reads the
/// final `message` item's `output_text` parts. A content-filter verdict
/// (`content_filters[].blocked`) sits alongside `output` as a normal part of the 200
/// response body, not as an HTTP error the way a chat-completions content filter surfaces —
/// this slice never inspects it; a 200 with no `message`/`output_text` content (e.g. the
/// model answered with only reasoning/search-call items, or a block suppressed the answer)
/// is simply treated as "no answer", same as any other empty response.
module internal AzureResponsesWire =
    let responsesUri (endpoint: string) : string =
        $"{endpoint.TrimEnd('/')}/openai/v1/responses"

    let buildBody (model: string) (query: string) : string =
        let root = JsonObject()
        root["model"] <- JsonValue.Create model
        let tools = JsonArray()
        let tool = JsonObject()
        tool["type"] <- JsonValue.Create "web_search"
        tools.Add tool
        root["tools"] <- tools
        root["input"] <- JsonValue.Create query
        root.ToJsonString()

    /// Responses' own usage field names (`input_tokens`/`output_tokens`/`total_tokens`) —
    /// see this module's DISCOVERY doc comment.
    let parseUsage (u: JsonElement) : TokenUsage =
        let intOf (name: string) =
            match u.TryGetProperty name with
            | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
            | _ -> 0
        { PromptTokens = intOf "input_tokens"
          CompletionTokens = intOf "output_tokens"
          TotalTokens = intOf "total_tokens" }

    /// Walks `output[]` for the (at most one expected) `type="message"` item, concatenates
    /// its `output_text` parts, and appends a "Источники:" bullet list built from every
    /// `url_citation` annotation (deduplicated by url, first-seen title wins). `None` when
    /// no message/text content was found.
    let private extractAnswer (root: JsonElement) : string option =
        match root.TryGetProperty "output" with
        | true, output when output.ValueKind = JsonValueKind.Array ->
            let textSb = StringBuilder()
            let citations = ResizeArray<string * string>()
            let seenUrls = HashSet<string>(StringComparer.Ordinal)
            for item in output.EnumerateArray() do
                match item.TryGetProperty "type" with
                | true, t when t.ValueKind = JsonValueKind.String && t.GetString() = "message" ->
                    match item.TryGetProperty "content" with
                    | true, content when content.ValueKind = JsonValueKind.Array ->
                        for part in content.EnumerateArray() do
                            match part.TryGetProperty "type" with
                            | true, pt when pt.ValueKind = JsonValueKind.String && pt.GetString() = "output_text" ->
                                match part.TryGetProperty "text" with
                                | true, txt when txt.ValueKind = JsonValueKind.String -> %textSb.Append(txt.GetString())
                                | _ -> ()
                                match part.TryGetProperty "annotations" with
                                | true, anns when anns.ValueKind = JsonValueKind.Array ->
                                    for ann in anns.EnumerateArray() do
                                        match ann.TryGetProperty "type" with
                                        | true, at when at.ValueKind = JsonValueKind.String && at.GetString() = "url_citation" ->
                                            let strOf (name: string) =
                                                match ann.TryGetProperty name with
                                                | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                                                | _ -> ""
                                            let url = strOf "url"
                                            let title = strOf "title"
                                            if url <> "" && seenUrls.Add url then
                                                citations.Add(title, url)
                                        | _ -> ()
                                | _ -> ()
                            | _ -> ()
                    | _ -> ()
                | _ -> ()
            let answer = textSb.ToString()
            if answer = "" then
                None
            else
                let withCitations =
                    if citations.Count = 0 then
                        answer
                    else
                        let lines = citations |> Seq.map (fun (title, url) -> $"- {title} ({url})") |> String.concat "\n"
                        $"{answer}\n\nИсточники:\n{lines}"
                Some withCitations
        | _ -> None

    let tryParseResponsesOutput (body: string) : Result<string * TokenUsage, string> =
        try
            use doc = JsonDocument.Parse(body)
            let root = doc.RootElement
            let usage =
                match root.TryGetProperty "usage" with
                | true, u when u.ValueKind = JsonValueKind.Object -> parseUsage u
                | _ -> { PromptTokens = 0; CompletionTokens = 0; TotalTokens = 0 }
            match extractAnswer root with
            | Some answer -> Ok(answer, usage)
            | None -> Error "no message/output_text content in response"
        with ex ->
            Error ex.Message

/// IWebSearch against the Azure Responses API's server-managed `web_search` tool — see
/// AzureResponsesWire's DISCOVERY doc comment for the probed wire shape. Reuses the SAME
/// named HttpClient (AzureFoundry.HttpClientName), auth convention (`api-key` header via
/// AzureWire.newRequest), and 429 retry policy (AzureHttp.sendWithRetry) as
/// AzureFoundryProvider — the "raw HTTP, no SDK" decision (D3) applies here too even though
/// the API surface itself differs.
type AzureResponsesWebSearch
    (
        httpFactory: IHttpClientFactory,
        options: IOptions<BotConfiguration>,
        usageRecorder: IUsageRecorder,
        logger: ILogger<AzureResponsesWebSearch>
    ) =

    interface IWebSearch with
        member _.Search(query: string, ctx: UsageContext, ct: CancellationToken) =
            task {
                let conf = options.Value
                if String.IsNullOrWhiteSpace conf.WebSearchModel then
                    logger.LogWarning("web_search requested but WEB_SEARCH_MODEL is unset")
                    return Error(LlmError.ApiError(0, "WEB_SEARCH_MODEL not configured"))
                else
                    use call =
                        new LlmCall(
                            "llm.web_search", "web_search", conf.WebSearchModel, false, conf.LlmPricingJson,
                            usageRecorder, ctx, logger)
                    let client = httpFactory.CreateClient(AzureFoundry.HttpClientName)
                    let uri = AzureResponsesWire.responsesUri conf.AzureResponsesEndpoint
                    let bodyJson = AzureResponsesWire.buildBody conf.WebSearchModel query
                    let makeRequest () = AzureWire.newRequest conf.AzureFoundryKey uri bodyJson

                    let! result, retries = AzureHttp.sendWithRetry logger conf.WebSearchModel client makeRequest ct
                    match result with
                    | Ok body ->
                        match AzureResponsesWire.tryParseResponsesOutput body with
                        | Ok(text, usage) ->
                            call.Succeeded(Some conf.WebSearchModel, Some usage, retries)
                            return Ok text
                        | Error parseError ->
                            logger.LogError("Unparseable web_search response ({Error}): {Body}", parseError, body)
                            let err = LlmError.ApiError(200, body)
                            call.Failed(err, retries)
                            return Error err
                    | Error err ->
                        AzureWire.logLlmError logger err
                        call.Failed(err, retries)
                        return Error err
            }
