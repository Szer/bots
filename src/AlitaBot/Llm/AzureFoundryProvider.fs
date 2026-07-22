namespace AlitaBot.Llm

open System
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open FSharp.Control
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open AlitaBot
open BotInfra

module AzureFoundry =
    /// Named HttpClient registered in Program.fs for all Azure Foundry calls.
    [<Literal>]
    let HttpClientName = "azure_foundry"

/// Azure OpenAI wire format helpers (raw HTTP per decision D3 — no Azure.AI.OpenAI SDK).
module internal AzureWire =
    [<Literal>]
    let ApiVersion = "2024-10-21"

    let chatUri (endpoint: string) (deployment: string) =
        $"{endpoint.TrimEnd('/')}/openai/deployments/{deployment}/chat/completions?api-version={ApiVersion}"

    let embeddingsUri (endpoint: string) (deployment: string) =
        $"{endpoint.TrimEnd('/')}/openai/deployments/{deployment}/embeddings?api-version={ApiVersion}"

    let transcriptionsUri (endpoint: string) (deployment: string) =
        $"{endpoint.TrimEnd('/')}/openai/deployments/{deployment}/audio/transcriptions?api-version={ApiVersion}"

    /// audio/speech (TTS) 404s against the real Azure resource on the shared `ApiVersion`
    /// above — confirmed empirically (Slice 9 stretch, `/say` real-test) — while chat/
    /// embeddings/transcriptions all work fine on it. "2024-08-01-preview" is the version
    /// S1's own hand-rolled curl verification used (VoiceRealTests.fs's synthesizeToFile,
    /// predating this file's ISpeech.Synthesize ever actually being called end-to-end
    /// against real Azure) and is confirmed working.
    [<Literal>]
    let SpeechApiVersion = "2024-08-01-preview"

    let speechUri (endpoint: string) (deployment: string) =
        $"{endpoint.TrimEnd('/')}/openai/deployments/{deployment}/audio/speech?api-version={SpeechApiVersion}"

    /// gpt-image-1 (images/generations, images/edits) needs a newer api-version than the
    /// chat/embeddings/audio routes above. NOTE: unverified against a real deployment —
    /// the alita-image deployment couldn't be created (quota denied for every gpt-image-*
    /// variant in this subscription/region at S3 deploy time, see AlitaBot/docs/TECH-DEBT.md)
    /// — so this api-version and the body shapes below are best-effort from Azure OpenAI's
    /// documented images API, not empirically confirmed the way the other endpoints are.
    [<Literal>]
    let ImagesApiVersion = "2025-04-01-preview"

    let imagesGenerationsUri (endpoint: string) (deployment: string) =
        $"{endpoint.TrimEnd('/')}/openai/deployments/{deployment}/images/generations?api-version={ImagesApiVersion}"

    let imagesEditsUri (endpoint: string) (deployment: string) =
        $"{endpoint.TrimEnd('/')}/openai/deployments/{deployment}/images/edits?api-version={ImagesApiVersion}"

    let buildImageGenBody (prompt: string) (size: string) (quality: string) =
        let root = JsonObject()
        root["prompt"] <- JsonValue.Create prompt
        root["size"] <- JsonValue.Create size
        root["quality"] <- JsonValue.Create quality
        root["n"] <- JsonValue.Create 1
        root.ToJsonString()

    /// gpt-image-1's usage block uses input_tokens/output_tokens (not chat's
    /// prompt_tokens/completion_tokens) — mapped onto the same TokenUsage shape.
    let parseImageUsage (u: JsonElement) : TokenUsage =
        let intOf (name: string) =
            match u.TryGetProperty name with
            | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
            | _ -> 0
        { PromptTokens = intOf "input_tokens"
          CompletionTokens = intOf "output_tokens"
          TotalTokens = intOf "total_tokens" }

    /// Parses `{"data":[{"b64_json":"..."}], "usage": {...}}` — dall-e-3 (the documented
    /// fallback model) omits `usage` entirely, which decodes to a zeroed TokenUsage.
    let tryParseImageResponse (body: string) : Result<byte[] * TokenUsage, string> =
        try
            use doc = JsonDocument.Parse(body)
            let root = doc.RootElement
            let b64 = root.GetProperty("data").[0].GetProperty("b64_json").GetString()
            let bytes = Convert.FromBase64String b64
            let usage =
                match root.TryGetProperty "usage" with
                | true, u when u.ValueKind = JsonValueKind.Object -> parseImageUsage u
                | _ -> { PromptTokens = 0; CompletionTokens = 0; TotalTokens = 0 }
            Ok(bytes, usage)
        with ex ->
            Error ex.Message

    /// Multipart images/edits request: source image + prompt + size/quality fields.
    let newImageEditRequest (apiKey: string) (uri: string) (prompt: string) (size: string) (quality: string) (sourceImage: byte[]) =
        let req = new HttpRequestMessage(HttpMethod.Post, uri)
        req.Headers.Add("api-key", apiKey)
        let content = new MultipartFormDataContent()
        let imageContent = new ByteArrayContent(sourceImage)
        imageContent.Headers.ContentType <- MediaTypeHeaderValue("image/png")
        content.Add(imageContent, "image", "source.png")
        content.Add(new StringContent(prompt), "prompt")
        content.Add(new StringContent(size), "size")
        content.Add(new StringContent(quality), "quality")
        req.Content <- content
        req

    let buildSpeechBody (text: string) (voice: string) =
        let root = JsonObject()
        root["input"] <- JsonValue.Create text
        root["voice"] <- JsonValue.Create voice
        root["response_format"] <- JsonValue.Create "opus"
        root.ToJsonString()

    /// Best-effort transcript extraction: response_format=json returns {"text": "..."};
    /// anything else (or an unparseable body) is treated as the raw text itself.
    let parseTranscript (body: string) : string =
        try
            use doc = JsonDocument.Parse(body)
            match doc.RootElement.TryGetProperty "text" with
            | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
            | _ -> body
        with _ -> body

    let newRequest (apiKey: string) (uri: string) (bodyJson: string) =
        let req = new HttpRequestMessage(HttpMethod.Post, uri)
        req.Headers.Add("api-key", apiKey)
        req.Content <- new StringContent(bodyJson, Encoding.UTF8, "application/json")
        req

    let private roleString =
        function
        | ChatRole.System -> "system"
        | ChatRole.User -> "user"
        | ChatRole.Assistant -> "assistant"
        | ChatRole.Tool -> "tool"

    let private contentNode (parts: ContentPart list) : JsonNode =
        match parts with
        | [ ContentPart.Text t ] -> JsonValue.Create(t)
        | parts ->
            let arr = JsonArray()
            for part in parts do
                let o = JsonObject()
                match part with
                | ContentPart.Text t ->
                    o["type"] <- JsonValue.Create "text"
                    o["text"] <- JsonValue.Create t
                | ContentPart.ImageUrl(url, detail) ->
                    o["type"] <- JsonValue.Create "image_url"
                    let img = JsonObject()
                    img["url"] <- JsonValue.Create url
                    match detail with
                    | Some d -> img["detail"] <- JsonValue.Create d
                    | None -> ()
                    o["image_url"] <- img
                arr.Add(o)
            arr

    let private messageNode (m: ChatMessage) =
        let o = JsonObject()
        o["role"] <- JsonValue.Create(roleString m.Role)
        if not m.Content.IsEmpty then
            o["content"] <- contentNode m.Content
        if not m.ToolCalls.IsEmpty then
            let calls = JsonArray()
            for tc in m.ToolCalls do
                let c = JsonObject()
                c["id"] <- JsonValue.Create tc.Id
                c["type"] <- JsonValue.Create "function"
                let f = JsonObject()
                f["name"] <- JsonValue.Create tc.Name
                f["arguments"] <- JsonValue.Create tc.ArgumentsJson
                c["function"] <- f
                calls.Add(c)
            o["tool_calls"] <- calls
        match m.ToolCallId with
        | Some id -> o["tool_call_id"] <- JsonValue.Create id
        | None -> ()
        o

    /// gpt-5 family rejects `max_tokens` (use `max_completion_tokens`) and its reasoning
    /// models ignore `temperature` — both are omitted entirely when unset.
    let buildChatBody (req: ChatRequest) (stream: bool) =
        let root = JsonObject()
        let messages = JsonArray()
        for m in req.Messages do
            messages.Add(messageNode m)
        root["messages"] <- messages
        if not req.Tools.IsEmpty then
            let tools = JsonArray()
            for t in req.Tools do
                let o = JsonObject()
                o["type"] <- JsonValue.Create "function"
                let f = JsonObject()
                f["name"] <- JsonValue.Create t.Name
                f["description"] <- JsonValue.Create t.Description
                f["parameters"] <- JsonNode.Parse(t.ParametersJsonSchema)
                o["function"] <- f
                tools.Add(o)
            root["tools"] <- tools
        match req.Temperature with
        | Some t -> root["temperature"] <- JsonValue.Create t
        | None -> ()
        match req.MaxTokens with
        | Some m -> root["max_completion_tokens"] <- JsonValue.Create m
        | None -> ()
        if stream then
            root["stream"] <- JsonValue.Create true
            let opts = JsonObject()
            opts["include_usage"] <- JsonValue.Create true
            root["stream_options"] <- opts
        root.ToJsonString()

    let buildEmbeddingsBody (texts: string list) =
        let root = JsonObject()
        let input = JsonArray()
        for t in texts do
            input.Add(JsonValue.Create t)
        root["input"] <- input
        root.ToJsonString()

    let parseFinish =
        function
        | "length" -> FinishReason.Length
        | "tool_calls" -> FinishReason.ToolCalls
        | "content_filter" -> FinishReason.ContentFilter
        | _ -> FinishReason.Stop

    let parseUsage (u: JsonElement) : TokenUsage =
        let intOf (name: string) =
            match u.TryGetProperty name with
            | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
            | _ -> 0
        { PromptTokens = intOf "prompt_tokens"
          CompletionTokens = intOf "completion_tokens"
          TotalTokens = intOf "total_tokens" }

    let private contentFilterDetail (body: string) =
        try
            use doc = JsonDocument.Parse(body)
            match doc.RootElement.TryGetProperty "error" with
            | true, err ->
                let strOf (name: string) =
                    match err.TryGetProperty name with
                    | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                    | _ -> ""
                if strOf "code" = "content_filter" then Some(strOf "message") else None
            | _ -> None
        with _ -> None

    let classifyError (status: int) (retryAfter: TimeSpan option) (body: string) =
        if status = 429 then
            LlmError.RateLimited retryAfter
        elif status = 400 then
            match contentFilterDetail body with
            | Some detail -> LlmError.ContentFiltered detail
            | None -> LlmError.ApiError(status, body)
        else
            LlmError.ApiError(status, body)

    let retryAfterOf (resp: HttpResponseMessage) : TimeSpan option =
        match resp.Headers.RetryAfter with
        | null -> None
        | ra ->
            if ra.Delta.HasValue then Some ra.Delta.Value
            elif ra.Date.HasValue then Some(ra.Date.Value - DateTimeOffset.UtcNow)
            else None

    /// Retry-After honored but capped at 10s; otherwise full-jitter backoff (250ms base).
    let backoffDelay (attempt: int) (retryAfter: TimeSpan option) =
        match retryAfter with
        | Some ra when ra > TimeSpan.Zero -> min ra (TimeSpan.FromSeconds 10.0)
        | _ -> TimeSpan.FromMilliseconds(float (Random.Shared.Next(1, 250 * (1 <<< attempt) + 1)))

    /// Content filter and rate limits are expected operational noise — Warning, not Error.
    let logLlmError (logger: ILogger) (err: LlmError) =
        match err with
        | LlmError.ContentFiltered detail ->
            logger.LogWarning("LLM request blocked by content filter: {Detail}", detail)
        | LlmError.RateLimited retryAfter ->
            logger.LogWarning("LLM rate limited (Retry-After: {RetryAfter})", retryAfter)
        | LlmError.ApiError(status, body) ->
            logger.LogError("LLM API error {Status}: {Body}", status, body)
        | LlmError.NetworkError message ->
            logger.LogError("LLM network error: {Message}", message)

    let tryParseChatResponse (body: string) : Result<ChatResponse * string option, string> =
        try
            use doc = JsonDocument.Parse(body)
            let root = doc.RootElement
            let model =
                match root.TryGetProperty "model" with
                | true, m when m.ValueKind = JsonValueKind.String -> Some(m.GetString())
                | _ -> None
            let choice = root.GetProperty("choices")[0]
            let message = choice.GetProperty "message"
            let text =
                match message.TryGetProperty "content" with
                | true, c when c.ValueKind = JsonValueKind.String -> c.GetString()
                | _ -> ""
            let strOf (el: JsonElement) (name: string) =
                if el.ValueKind <> JsonValueKind.Object then ""
                else
                    match el.TryGetProperty name with
                    | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                    | _ -> ""
            let toolCalls =
                match message.TryGetProperty "tool_calls" with
                | true, tcs when tcs.ValueKind = JsonValueKind.Array ->
                    [ for tc in tcs.EnumerateArray() ->
                        let f =
                            match tc.TryGetProperty "function" with
                            | true, f -> f
                            | _ -> JsonElement()
                        { Id = strOf tc "id"
                          Name = strOf f "name"
                          ArgumentsJson = strOf f "arguments" } ]
                | _ -> []
            let finish =
                match choice.TryGetProperty "finish_reason" with
                | true, f when f.ValueKind = JsonValueKind.String -> parseFinish (f.GetString())
                | _ -> FinishReason.Stop
            let usage =
                match root.TryGetProperty "usage" with
                | true, u when u.ValueKind = JsonValueKind.Object -> Some(parseUsage u)
                | _ -> None
            Ok({ Text = text; ToolCalls = toolCalls; FinishReason = finish; Usage = usage }, model)
        with ex ->
            Error ex.Message

    let tryParseEmbeddings (body: string) : Result<float32[][] * TokenUsage option, string> =
        try
            use doc = JsonDocument.Parse(body)
            let root = doc.RootElement
            let vectors =
                root.GetProperty("data").EnumerateArray()
                |> Seq.map (fun d ->
                    d.GetProperty("embedding").EnumerateArray()
                    |> Seq.map (fun v -> v.GetSingle())
                    |> Seq.toArray)
                |> Seq.toArray
            let usage =
                match root.TryGetProperty "usage" with
                | true, u when u.ValueKind = JsonValueKind.Object -> Some(parseUsage u)
                | _ -> None
            Ok(vectors, usage)
        with ex ->
            Error ex.Message

module internal AzureHttp =
    /// Sends a non-streaming request with the D3 429 policy: max 2 retries, honor
    /// Retry-After capped at 10s, full-jitter backoff otherwise. Returns the response
    /// body (or in-band error) plus the number of retries performed.
    let sendWithRetry
        (client: HttpClient)
        (makeRequest: unit -> HttpRequestMessage)
        (ct: CancellationToken)
        : Task<Result<string, LlmError> * int> =
        task {
            let maxRetries = 2
            let mutable retries = 0
            let mutable result: Result<string, LlmError> option = None
            while result.IsNone do
                try
                    use req = makeRequest ()
                    use! resp = client.SendAsync(req, ct)
                    let! body = resp.Content.ReadAsStringAsync(ct)
                    if resp.IsSuccessStatusCode then
                        result <- Some(Ok body)
                    else
                        let err = AzureWire.classifyError (int resp.StatusCode) (AzureWire.retryAfterOf resp) body
                        match err with
                        | LlmError.RateLimited retryAfter when retries < maxRetries ->
                            do! Task.Delay(AzureWire.backoffDelay retries retryAfter, ct)
                            retries <- retries + 1
                        | _ -> result <- Some(Error err)
                with ex ->
                    result <- Some(Error(LlmError.NetworkError ex.Message))
            return result.Value, retries
        }

    /// Same 429 policy as `sendWithRetry`, but reads the success body as raw bytes
    /// (audio) instead of a UTF-8 string — TTS responses are binary and would be
    /// corrupted by a string round-trip.
    let sendBinaryWithRetry
        (client: HttpClient)
        (makeRequest: unit -> HttpRequestMessage)
        (ct: CancellationToken)
        : Task<Result<byte[], LlmError> * int> =
        task {
            let maxRetries = 2
            let mutable retries = 0
            let mutable result: Result<byte[], LlmError> option = None
            while result.IsNone do
                try
                    use req = makeRequest ()
                    use! resp = client.SendAsync(req, ct)
                    if resp.IsSuccessStatusCode then
                        let! bytes = resp.Content.ReadAsByteArrayAsync(ct)
                        result <- Some(Ok bytes)
                    else
                        let! body = resp.Content.ReadAsStringAsync(ct)
                        let err = AzureWire.classifyError (int resp.StatusCode) (AzureWire.retryAfterOf resp) body
                        match err with
                        | LlmError.RateLimited retryAfter when retries < maxRetries ->
                            do! Task.Delay(AzureWire.backoffDelay retries retryAfter, ct)
                            retries <- retries + 1
                        | _ -> result <- Some(Error err)
                with ex ->
                    result <- Some(Error(LlmError.NetworkError ex.Message))
            return result.Value, retries
        }

/// Mutable accumulator for one streamed tool call (fragments arrive across chunks).
type internal ToolCallAcc() =
    member val Id = "" with get, set
    member val Name = "" with get, set
    member val Args = StringBuilder() with get

/// IChatCompletion against Azure AI Foundry chat-completions, raw HTTP + SSE.
/// Endpoint/deployment/pricing come from IOptions<BotConfiguration> per call (hot-reload).
type AzureFoundryChat(httpFactory: IHttpClientFactory, options: IOptions<BotConfiguration>, usageRecorder: IUsageRecorder, logger: ILogger<AzureFoundryChat>) =

    interface IChatCompletion with
        member _.Complete(request: ChatRequest, ctx: UsageContext, ct: CancellationToken) =
            task {
                let conf = options.Value
                use call = new LlmCall("llm.chat", "chat", request.Deployment, false, conf.LlmPricingJson, usageRecorder, ctx, logger)
                let client = httpFactory.CreateClient(AzureFoundry.HttpClientName)
                let uri = AzureWire.chatUri conf.AzureFoundryEndpoint request.Deployment
                let bodyJson = AzureWire.buildChatBody request false
                let! result, retries =
                    AzureHttp.sendWithRetry client (fun () -> AzureWire.newRequest conf.AzureFoundryKey uri bodyJson) ct
                match result with
                | Ok body ->
                    match AzureWire.tryParseChatResponse body with
                    | Ok(response, model) ->
                        call.Succeeded(model, response.Usage, retries)
                        return Ok response
                    | Error parseError ->
                        logger.LogError("Unparseable chat completion response ({Error}): {Body}", parseError, body)
                        let err = LlmError.ApiError(200, body)
                        call.Failed(err, retries)
                        return Error err
                | Error err ->
                    AzureWire.logLlmError logger err
                    call.Failed(err, retries)
                    return Error err
            }

        // NEVER retries (D3): a 429 up front or a disconnect mid-stream surfaces as an
        // in-band Failed chunk — the stream itself never throws.
        member _.CompleteStream(request: ChatRequest, ctx: UsageContext, ct: CancellationToken) =
            taskSeq {
                let conf = options.Value
                use call = new LlmCall("llm.chat", "chat", request.Deployment, true, conf.LlmPricingJson, usageRecorder, ctx, logger)
                let client = httpFactory.CreateClient(AzureFoundry.HttpClientName)
                let uri = AzureWire.chatUri conf.AzureFoundryEndpoint request.Deployment
                let bodyJson = AzureWire.buildChatBody request true

                let! opened =
                    task {
                        try
                            use req = AzureWire.newRequest conf.AzureFoundryKey uri bodyJson
                            let! resp = client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                            return Ok resp
                        with ex ->
                            return Error(LlmError.NetworkError ex.Message)
                    }

                match opened with
                | Error err ->
                    AzureWire.logLlmError logger err
                    call.Failed(err, 0)
                    yield ChatChunk.Failed err
                | Ok httpResp ->
                    use httpResp = httpResp
                    if not httpResp.IsSuccessStatusCode then
                        let! errBody =
                            task {
                                try
                                    return! httpResp.Content.ReadAsStringAsync(ct)
                                with _ ->
                                    return ""
                            }
                        let err =
                            AzureWire.classifyError (int httpResp.StatusCode) (AzureWire.retryAfterOf httpResp) errBody
                        AzureWire.logLlmError logger err
                        call.Failed(err, 0)
                        yield ChatChunk.Failed err
                    else
                        let! readerRes =
                            task {
                                try
                                    let! s = httpResp.Content.ReadAsStreamAsync(ct)
                                    return Ok(new StreamReader(s, Encoding.UTF8))
                                with ex ->
                                    return Error(LlmError.NetworkError ex.Message)
                            }
                        match readerRes with
                        | Error err ->
                            AzureWire.logLlmError logger err
                            call.Failed(err, 0)
                            yield ChatChunk.Failed err
                        | Ok reader ->
                            use reader = reader
                            let text = StringBuilder()
                            let tools = SortedDictionary<int, ToolCallAcc>()
                            let mutable usage: TokenUsage option = None
                            let mutable model: string option = None
                            let mutable finish: FinishReason option = None
                            let mutable sawDone = false
                            let mutable eof = false
                            let mutable streamError: LlmError option = None

                            while not sawDone && not eof && streamError.IsNone do
                                let! lineRes =
                                    task {
                                        try
                                            let! line = reader.ReadLineAsync(ct)
                                            return Ok line
                                        with ex ->
                                            return Error(LlmError.NetworkError ex.Message)
                                    }
                                match lineRes with
                                | Error e -> streamError <- Some e
                                | Ok null -> eof <- true
                                | Ok line when line.StartsWith("data:", StringComparison.Ordinal) ->
                                    let payload = line.Substring(5).Trim()
                                    if payload = "[DONE]" then
                                        sawDone <- true
                                    else
                                        let parsed = (try Some(JsonDocument.Parse payload) with _ -> None)
                                        match parsed with
                                        | None -> ()
                                        | Some doc ->
                                            use doc = doc
                                            let root = doc.RootElement
                                            match root.TryGetProperty "model" with
                                            | true, m when m.ValueKind = JsonValueKind.String ->
                                                model <- Some(m.GetString())
                                            | _ -> ()
                                            // usage arrives in the final chunk (empty choices) via stream_options
                                            match root.TryGetProperty "usage" with
                                            | true, u when u.ValueKind = JsonValueKind.Object ->
                                                usage <- Some(AzureWire.parseUsage u)
                                            | _ -> ()
                                            match root.TryGetProperty "choices" with
                                            | true, choices when
                                                choices.ValueKind = JsonValueKind.Array && choices.GetArrayLength() > 0 ->
                                                let c0 = choices[0]
                                                match c0.TryGetProperty "finish_reason" with
                                                | true, f when f.ValueKind = JsonValueKind.String ->
                                                    finish <- Some(AzureWire.parseFinish (f.GetString()))
                                                | _ -> ()
                                                match c0.TryGetProperty "delta" with
                                                | true, delta ->
                                                    match delta.TryGetProperty "content" with
                                                    | true, piece when piece.ValueKind = JsonValueKind.String ->
                                                        let s = piece.GetString()
                                                        if s.Length > 0 then
                                                            %text.Append(s)
                                                            yield ChatChunk.TextDelta s
                                                    | _ -> ()
                                                    match delta.TryGetProperty "tool_calls" with
                                                    | true, tcs when tcs.ValueKind = JsonValueKind.Array ->
                                                        for tc in tcs.EnumerateArray() do
                                                            let idx =
                                                                (match tc.TryGetProperty "index" with
                                                                 | true, i when i.ValueKind = JsonValueKind.Number -> i.GetInt32()
                                                                 | _ -> 0)
                                                            let acc =
                                                                (match tools.TryGetValue idx with
                                                                 | true, a -> a
                                                                 | _ ->
                                                                     let a = ToolCallAcc()
                                                                     tools[idx] <- a
                                                                     a)
                                                            let strFrag (el: JsonElement) (name: string) =
                                                                match el.TryGetProperty name with
                                                                | true, v when v.ValueKind = JsonValueKind.String ->
                                                                    Some(v.GetString())
                                                                | _ -> None
                                                            let idFrag = strFrag tc "id"
                                                            let nameFrag, argsFrag =
                                                                (match tc.TryGetProperty "function" with
                                                                 | true, f -> strFrag f "name", (strFrag f "arguments" |> Option.defaultValue "")
                                                                 | _ -> None, "")
                                                            idFrag |> Option.iter (fun v -> acc.Id <- v)
                                                            nameFrag |> Option.iter (fun v -> acc.Name <- v)
                                                            if argsFrag.Length > 0 then
                                                                %acc.Args.Append(argsFrag)
                                                            yield ChatChunk.ToolCallDelta(idx, idFrag, nameFrag, argsFrag)
                                                    | _ -> ()
                                                | _ -> ()
                                            | _ -> ()
                                | Ok _ -> () // SSE keep-alives / empty separator lines

                            if streamError.IsSome then
                                let err = streamError.Value
                                AzureWire.logLlmError logger err
                                call.Failed(err, 0)
                                yield ChatChunk.Failed err
                            elif not sawDone then
                                let err = LlmError.NetworkError "SSE stream ended before [DONE]"
                                AzureWire.logLlmError logger err
                                call.Failed(err, 0)
                                yield ChatChunk.Failed err
                            else
                                let toolCalls =
                                    [ for KeyValue(_, a) in tools ->
                                        { Id = a.Id; Name = a.Name; ArgumentsJson = a.Args.ToString() } ]
                                let response =
                                    { Text = text.ToString()
                                      ToolCalls = toolCalls
                                      FinishReason = finish |> Option.defaultValue FinishReason.Stop
                                      Usage = usage }
                                call.Succeeded(model, usage, 0)
                                yield ChatChunk.Completed response
            }

type AzureFoundryEmbeddings(httpFactory: IHttpClientFactory, options: IOptions<BotConfiguration>, usageRecorder: IUsageRecorder, logger: ILogger<AzureFoundryEmbeddings>) =

    interface IEmbeddings with
        member _.Embed(deployment: string, texts: string list, ctx: UsageContext, ct: CancellationToken) =
            task {
                let conf = options.Value
                use call = new LlmCall("llm.embeddings", "embedding", deployment, false, conf.LlmPricingJson, usageRecorder, ctx, logger)
                let client = httpFactory.CreateClient(AzureFoundry.HttpClientName)
                let uri = AzureWire.embeddingsUri conf.AzureFoundryEndpoint deployment
                let bodyJson = AzureWire.buildEmbeddingsBody texts
                let! result, retries =
                    AzureHttp.sendWithRetry client (fun () -> AzureWire.newRequest conf.AzureFoundryKey uri bodyJson) ct
                match result with
                | Ok body ->
                    match AzureWire.tryParseEmbeddings body with
                    | Ok(vectors, usage) ->
                        call.Succeeded(None, usage, retries)
                        return Ok vectors
                    | Error parseError ->
                        logger.LogError("Unparseable embeddings response ({Error}): {Body}", parseError, body)
                        let err = LlmError.ApiError(200, body)
                        call.Failed(err, retries)
                        return Error err
                | Error err ->
                    AzureWire.logLlmError logger err
                    call.Failed(err, retries)
                    return Error err
            }

/// ISpeech against Azure AI Foundry's audio endpoints — STT via
/// audio/transcriptions (multipart), TTS via audio/speech (JSON in, binary out).
/// Deployment names come from IOptions<BotConfiguration> (STT_DEPLOYMENT/TTS_DEPLOYMENT).
type AzureFoundrySpeech(httpFactory: IHttpClientFactory, options: IOptions<BotConfiguration>, usageRecorder: IUsageRecorder, logger: ILogger<AzureFoundrySpeech>) =

    interface ISpeech with
        member _.Transcribe(audio: byte[], ctx: UsageContext, ct: CancellationToken) =
            task {
                let conf = options.Value
                use call = new LlmCall("llm.stt", "stt", conf.SttDeployment, false, conf.LlmPricingJson, usageRecorder, ctx, logger)
                let client = httpFactory.CreateClient(AzureFoundry.HttpClientName)
                let uri = AzureWire.transcriptionsUri conf.AzureFoundryEndpoint conf.SttDeployment

                let makeRequest () =
                    let req = new HttpRequestMessage(HttpMethod.Post, uri)
                    req.Headers.Add("api-key", conf.AzureFoundryKey)
                    let content = new MultipartFormDataContent()
                    let fileContent = new ByteArrayContent(audio)
                    fileContent.Headers.ContentType <- MediaTypeHeaderValue("audio/ogg")
                    content.Add(fileContent, "file", "voice.ogg")
                    content.Add(new StringContent("json"), "response_format")
                    req.Content <- content
                    req

                let! result, retries = AzureHttp.sendWithRetry client makeRequest ct
                match result with
                | Ok body ->
                    let text = AzureWire.parseTranscript body
                    call.Succeeded(None, None, retries)
                    return Ok text
                | Error err ->
                    AzureWire.logLlmError logger err
                    call.Failed(err, retries)
                    return Error err
            }

        member _.Synthesize(text: string, voice: string option, ctx: UsageContext, ct: CancellationToken) =
            task {
                let conf = options.Value
                use call = new LlmCall("llm.tts", "tts", conf.TtsDeployment, false, conf.LlmPricingJson, usageRecorder, ctx, logger)
                let client = httpFactory.CreateClient(AzureFoundry.HttpClientName)
                let uri = AzureWire.speechUri conf.AzureFoundryEndpoint conf.TtsDeployment
                let voiceName = voice |> Option.defaultValue "alloy"
                let bodyJson = AzureWire.buildSpeechBody text voiceName
                let makeRequest () = AzureWire.newRequest conf.AzureFoundryKey uri bodyJson

                let! result, retries = AzureHttp.sendBinaryWithRetry client makeRequest ct
                match result with
                | Ok bytes ->
                    call.Succeeded(None, None, retries)
                    return Ok bytes
                | Error err ->
                    AzureWire.logLlmError logger err
                    call.Failed(err, retries)
                    return Error err
            }

/// IImageGen against Azure AI Foundry's images endpoints — text->image via
/// images/generations, img2img via images/edits (multipart, source image attached).
/// Deployment/size/quality come from IOptions<BotConfiguration> (hot-reload).
type AzureFoundryImageGen(httpFactory: IHttpClientFactory, options: IOptions<BotConfiguration>, usageRecorder: IUsageRecorder, logger: ILogger<AzureFoundryImageGen>) =

    interface IImageGen with
        member _.Generate(prompt: string, sourceImage: byte[] option, ctx: UsageContext, ct: CancellationToken) =
            task {
                let conf = options.Value
                use call = new ImageCall(conf.ImageDeployment, conf.ImageQuality, conf.ImageSize, conf.LlmPricingJson, usageRecorder, ctx, logger)
                let client = httpFactory.CreateClient(AzureFoundry.HttpClientName)

                let makeRequest () =
                    match sourceImage with
                    | Some src ->
                        let uri = AzureWire.imagesEditsUri conf.AzureFoundryEndpoint conf.ImageDeployment
                        AzureWire.newImageEditRequest conf.AzureFoundryKey uri prompt conf.ImageSize conf.ImageQuality src
                    | None ->
                        let uri = AzureWire.imagesGenerationsUri conf.AzureFoundryEndpoint conf.ImageDeployment
                        let bodyJson = AzureWire.buildImageGenBody prompt conf.ImageSize conf.ImageQuality
                        AzureWire.newRequest conf.AzureFoundryKey uri bodyJson

                let! result, retries = AzureHttp.sendWithRetry client makeRequest ct
                match result with
                | Ok body ->
                    match AzureWire.tryParseImageResponse body with
                    | Ok(bytes, usage) ->
                        call.Succeeded(usage)
                        return Ok(bytes, usage)
                    | Error parseError ->
                        logger.LogError("Unparseable image generation response ({Error}): {Body}", parseError, body)
                        let err = LlmError.ApiError(200, body)
                        call.Failed(err)
                        return Error err
                | Error err ->
                    AzureWire.logLlmError logger err
                    call.Failed(err)
                    return Error err
            }
