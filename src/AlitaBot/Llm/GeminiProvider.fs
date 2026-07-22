namespace AlitaBot.Llm

open System
open System.Collections.Generic
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open AlitaBot
open BotInfra

module Gemini =
    /// Named HttpClient registered in Program.fs for all Gemini API calls.
    [<Literal>]
    let HttpClientName = "gemini"

/// Google Generative Language API v1beta wire format (raw HTTP, same D3-style idiom as
/// AzureFoundryProvider's AzureWire — no google-genai SDK).
///
/// DISCOVERY (2026-07-22, `curl` against the real API, see PR description for the full
/// transcript): `GET /v1beta/models` with `x-goog-api-key` returned this key's full model
/// roster. Image-capable ("Nano Banana") tiers, oldest -> newest:
///   gemini-2.5-flash-image        "Nano Banana"      (generateContent)
///   gemini-3-pro-image[-preview]  "Nano Banana Pro"   (generateContent)
///   gemini-3.1-flash-image[-preview] / -lite-image  "Nano Banana 2" / "Nano Banana 2 Lite"
/// GEMINI_IMAGE_MODEL defaults to "gemini-3.1-flash-image" — the newest non-preview
/// "Nano Banana 2" tier (§ discovery: prefer newest, prefer non-preview when both exist).
/// Music: "lyria-3-pro-preview" (full track) and "lyria-3-clip-preview" (short clip), both
/// `generateContent`-capable on this key — no separate "Lyria RealTime" streaming model was
/// listed. GEMINI_MUSIC_MODEL defaults to "lyria-3-pro-preview".
///
/// WIRE VERIFICATION: `POST /v1beta/models/{model}:generateContent` with
/// `{"contents":[{"parts":[{"text":...}]}]}` (+ an `inline_data` part for img2img, +
/// `generationConfig.responseModalities` for image gen) was confirmed schema-VALID against
/// the real API — a deliberately malformed body 400s ("Unknown name ... Cannot find
/// field"), while every request shape below reaches the quota gate and 429s
/// ("RESOURCE_EXHAUSTED ... free_tier_requests, limit: 0"). That 429 is a genuine billing
/// gate specific to THIS key's Google Cloud project (no billing account attached) — text-
/// only chat models ("-latest"/"-preview" aliases) on the SAME key return 200s fine, and a
/// malformed body still 400s before quota is even consulted, so this isn't a transient rate
/// limit. Practical effect: exactly like Azure's gpt-image-1 quota (AzureWire's doc comment
/// above) — the REQUEST shapes here are empirically confirmed, but no real SUCCESSFUL
/// response body was ever observed, so the response-parsing shapes below (camelCase
/// `inlineData`/`mimeType`, `usageMetadata`) are best-effort from Google's public API
/// documentation, not empirically confirmed the way e.g. AzureWire's chat completions are.
/// Lyria's audio container format (likely WAV/PCM per the task brief) is UNVERIFIED for the
/// same reason — GeminiMusicGen below re-encodes via ffmpeg same as BotService's `/say`,
/// which degrades gracefully (falls back to sending the raw bytes as-is) if the real
/// container turns out to need no conversion, or ffmpeg can't parse it.
module internal GeminiWire =
    [<Literal>]
    let ApiVersion = "v1beta"

    let generateContentUri (baseUrl: string) (model: string) =
        $"{baseUrl.TrimEnd('/')}/{ApiVersion}/models/{model}:generateContent"

    /// `contents: [{parts: [{text}, {inline_data}?]}]`, `generationConfig.responseModalities
    /// = [IMAGE, TEXT]` — the flag Google's docs describe for image-output models (Azure's
    /// gpt-image-1 has no equivalent; Gemini's generateContent is otherwise a plain chat
    /// endpoint, so this is what tells it to emit image parts back). `sourceImage = Some` adds
    /// an `inline_data` part (base64 PNG) ahead of the text — img2img via the SAME endpoint,
    /// no separate "edits" route the way Azure has (confirmed schema-valid via curl, see
    /// module doc comment above).
    let buildImageGenBody (prompt: string) (sourceImage: byte[] option) : string =
        let root = JsonObject()
        let contents = JsonArray()
        let content = JsonObject()
        let parts = JsonArray()
        match sourceImage with
        | Some bytes ->
            let imgPart = JsonObject()
            let inlineData = JsonObject()
            inlineData["mime_type"] <- JsonValue.Create "image/png"
            inlineData["data"] <- JsonValue.Create(Convert.ToBase64String bytes)
            imgPart["inline_data"] <- inlineData
            parts.Add imgPart
        | None -> ()
        let textPart = JsonObject()
        textPart["text"] <- JsonValue.Create prompt
        parts.Add textPart
        content["parts"] <- parts
        contents.Add content
        root["contents"] <- contents
        let genConfig = JsonObject()
        let modalities = JsonArray()
        modalities.Add(JsonValue.Create "IMAGE")
        modalities.Add(JsonValue.Create "TEXT")
        genConfig["responseModalities"] <- modalities
        root["generationConfig"] <- genConfig
        root.ToJsonString()

    /// `contents: [{parts: [{text = stylePrompt + lyrics}]}]` — no `responseModalities`:
    /// Lyria models are audio-out by their own model card, unlike the general-purpose Nano
    /// Banana chat models above (unverified — see module doc comment).
    let buildMusicGenBody (prompt: string) : string =
        let root = JsonObject()
        let contents = JsonArray()
        let content = JsonObject()
        let parts = JsonArray()
        let textPart = JsonObject()
        textPart["text"] <- JsonValue.Create prompt
        parts.Add textPart
        content["parts"] <- parts
        contents.Add content
        root["contents"] <- contents
        root.ToJsonString()

    let parseUsage (u: JsonElement) : TokenUsage =
        let intOf (name: string) =
            match u.TryGetProperty name with
            | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
            | _ -> 0
        { PromptTokens = intOf "promptTokenCount"
          CompletionTokens = intOf "candidatesTokenCount"
          TotalTokens = intOf "totalTokenCount" }

    /// Extracts every `inlineData` part (base64 `data` + `mimeType`) from
    /// `candidates[0].content.parts`, plus `usageMetadata` (mapped onto TokenUsage — see
    /// module doc comment on why this shape is best-effort). A `promptFeedback.blockReason`
    /// (safety block, no candidates at all) or a candidate `finishReason = "SAFETY"` both
    /// short-circuit to `Error "blocked: ..."` — the caller maps this to
    /// `LlmError.ContentFiltered`, mirroring AzureWire's content-filter handling.
    let tryParseGenerateContentResponse (body: string) : Result<(byte[] * string) list * TokenUsage, string> =
        try
            use doc = JsonDocument.Parse(body)
            let root = doc.RootElement
            let usage =
                match root.TryGetProperty "usageMetadata" with
                | true, u when u.ValueKind = JsonValueKind.Object -> parseUsage u
                | _ -> { PromptTokens = 0; CompletionTokens = 0; TotalTokens = 0 }
            match root.TryGetProperty "promptFeedback" with
            | true, pf ->
                match pf.TryGetProperty "blockReason" with
                | true, br when br.ValueKind = JsonValueKind.String ->
                    Error $"blocked: {br.GetString()}"
                | _ -> Ok(root, usage)
            | _ -> Ok(root, usage)
            |> Result.bind (fun (root, usage) ->
                match root.TryGetProperty "candidates" with
                | true, cs when cs.ValueKind = JsonValueKind.Array && cs.GetArrayLength() > 0 ->
                    let c0 = cs[0]
                    let finishIsSafety =
                        match c0.TryGetProperty "finishReason" with
                        | true, f when f.ValueKind = JsonValueKind.String -> f.GetString() = "SAFETY"
                        | _ -> false
                    if finishIsSafety then
                        Error "blocked: SAFETY finishReason"
                    else
                        let parts =
                            match c0.TryGetProperty "content" with
                            | true, content ->
                                match content.TryGetProperty "parts" with
                                | true, ps when ps.ValueKind = JsonValueKind.Array -> ps.EnumerateArray() |> Seq.toList
                                | _ -> []
                            | _ -> []
                        let inlineParts =
                            parts
                            |> List.choose (fun p ->
                                match p.TryGetProperty "inlineData" with
                                | true, inl ->
                                    let dataB64 =
                                        match inl.TryGetProperty "data" with
                                        | true, d when d.ValueKind = JsonValueKind.String -> d.GetString()
                                        | _ -> ""
                                    let mime =
                                        match inl.TryGetProperty "mimeType" with
                                        | true, m when m.ValueKind = JsonValueKind.String -> m.GetString()
                                        | _ -> "application/octet-stream"
                                    if dataB64 = "" then None
                                    else
                                        try Some(Convert.FromBase64String dataB64, mime) with _ -> None
                                | _ -> None)
                        if inlineParts.IsEmpty then
                            Error "no inlineData parts in response"
                        else
                            Ok(inlineParts, usage)
                | _ -> Error "no candidates in response")
        with ex ->
            Error ex.Message

    /// Best-effort `Retry-After` equivalent: Gemini 429s don't carry a standard
    /// `Retry-After` HTTP header (confirmed via curl -D against the real API), but DO carry
    /// a `details[].retryDelay` field in the JSON body (e.g. `"retryDelay": "30s"`) — parsed
    /// here the same way AzureWire.retryAfterOf reads the HTTP header.
    let retryDelayFromBody (body: string) : TimeSpan option =
        try
            use doc = JsonDocument.Parse(body)
            match doc.RootElement.TryGetProperty "error" with
            | true, err ->
                match err.TryGetProperty "details" with
                | true, details when details.ValueKind = JsonValueKind.Array ->
                    details.EnumerateArray()
                    |> Seq.tryPick (fun d ->
                        match d.TryGetProperty "retryDelay" with
                        | true, rd when rd.ValueKind = JsonValueKind.String ->
                            let s = rd.GetString().TrimEnd('s')
                            match Double.TryParse(s, Globalization.CultureInfo.InvariantCulture) with
                            | true, seconds -> Some(TimeSpan.FromSeconds seconds)
                            | _ -> None
                        | _ -> None)
                | _ -> None
            | _ -> None
        with _ -> None

    /// `{"error":{"code":...,"status":"...","message":"..."}}` — Gemini's error envelope
    /// (distinct from Azure's `{"error":{"code":"content_filter",...}}` shape).
    let classifyError (status: int) (body: string) : LlmError =
        if status = 429 then
            LlmError.RateLimited(retryDelayFromBody body)
        elif status = 400 then
            // Gemini surfaces safety blocks as a normal 200 with promptFeedback/finishReason
            // (handled in tryParseGenerateContentResponse) — a genuine 400 here is a request-
            // shape error, not a content filter.
            LlmError.ApiError(status, body)
        else
            LlmError.ApiError(status, body)

    /// Logs the terminal `LlmError` for a Gemini call — always at Warning, always tagged
    /// with `model` (user rule: "read errors before retrying", see AGENTS.md commit
    /// history) — right before the caller returns the failure that becomes the
    /// user-facing RU fallback ("Не получилось сочинить/нарисовать 🙁"). For `ApiError`
    /// this duplicates what `GeminiHttp.sendWithRetry` already logged per-attempt (see its
    /// doc comment) — kept here too since this is also the ONLY log line for a terminal
    /// `NetworkError`/`RateLimited` that ran out of retries without ever going through
    /// `sendWithRetry`'s non-success branch (e.g. a `NetworkError` from the `with ex ->`
    /// catch, which already logs its own Warning — see there).
    let logError (logger: ILogger) (model: string) (err: LlmError) =
        match err with
        | LlmError.ContentFiltered detail ->
            logger.LogWarning("Gemini request blocked: model={Model} detail={Detail}", model, detail)
        | LlmError.RateLimited retryAfter ->
            logger.LogWarning("Gemini rate limited: model={Model} retryDelay={RetryAfter}", model, retryAfter)
        | LlmError.ApiError(status, body) ->
            logger.LogWarning("Gemini API error: model={Model} status={Status} body={Body}", model, status, body)
        | LlmError.NetworkError message ->
            logger.LogWarning("Gemini network error: model={Model} message={Message}", model, message)

    let newRequest (apiKey: string) (uri: string) (bodyJson: string) =
        let req = new HttpRequestMessage(HttpMethod.Post, uri)
        req.Headers.Add("x-goog-api-key", apiKey)
        req.Content <- new StringContent(bodyJson, Encoding.UTF8, "application/json")
        req

module internal GeminiHttp =
    /// Same 429 policy as AzureHttp.sendWithRetry (D3): max 2 retries, honor the parsed
    /// retryDelay capped at 10s, full-jitter backoff otherwise.
    ///
    /// `logger`/`model` exist so every non-success response is logged in FULL — status,
    /// model, attempt number, and the raw response body — at Warning, BEFORE the retry-vs-
    /// give-up decision is made (user rule: "read errors before retrying"). Without this,
    /// a 429 that gets silently retried into a delay-and-retry loop never has its response
    /// body logged anywhere unless every retry is also exhausted; a network exception is
    /// logged the same way from the `with ex ->` branch.
    let sendWithRetry
        (logger: ILogger)
        (model: string)
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
                        logger.LogWarning(
                            "Gemini API non-success response: model={Model} status={Status} attempt={Attempt} body={Body}",
                            model, int resp.StatusCode, retries + 1, body)
                        let err = GeminiWire.classifyError (int resp.StatusCode) body
                        match err with
                        | LlmError.RateLimited retryAfter when retries < maxRetries ->
                            let delay =
                                match retryAfter with
                                | Some ra when ra > TimeSpan.Zero -> min ra (TimeSpan.FromSeconds 10.0)
                                | _ -> TimeSpan.FromMilliseconds(float (Random.Shared.Next(1, 250 * (1 <<< retries) + 1)))
                            do! Task.Delay(delay, ct)
                            retries <- retries + 1
                        | _ -> result <- Some(Error err)
                with ex ->
                    logger.LogWarning(ex, "Gemini network error: model={Model} attempt={Attempt}", model, retries + 1)
                    result <- Some(Error(LlmError.NetworkError ex.Message))
            return result.Value, retries
        }

/// GeminiImageGen against Google's generateContent endpoint — Nano Banana. `sourceImage =
/// None` is text-to-image; `Some bytes` folds the source PNG in as an `inline_data` part on
/// the SAME endpoint (no separate edits route, unlike Azure — see GeminiWire's doc comment).
/// Selected by ImageGenRouter below when IMAGE_PROVIDER=gemini.
type GeminiImageGen(httpFactory: IHttpClientFactory, options: IOptions<BotConfiguration>, usageRecorder: IUsageRecorder, logger: ILogger<GeminiImageGen>) =

    interface IImageGen with
        member _.Generate(prompt: string, sourceImage: byte[] option, ctx: UsageContext, ct: CancellationToken) =
            task {
                let conf = options.Value
                if String.IsNullOrWhiteSpace conf.GeminiApiKey then
                    logger.LogWarning("Gemini image generation requested but GEMINI_API_KEY is unset")
                    return Error(LlmError.ApiError(0, "GEMINI_API_KEY not configured"))
                else
                    use call =
                        new ImageCall(
                            "gemini",
                            conf.GeminiImageModel,
                            None,
                            None,
                            conf.LlmPricingJson,
                            usageRecorder,
                            ctx,
                            logger)
                    let client = httpFactory.CreateClient(Gemini.HttpClientName)
                    let uri = GeminiWire.generateContentUri conf.GeminiBaseUrl conf.GeminiImageModel
                    let bodyJson = GeminiWire.buildImageGenBody prompt sourceImage
                    let makeRequest () = GeminiWire.newRequest conf.GeminiApiKey uri bodyJson

                    let! result, _retries = GeminiHttp.sendWithRetry logger conf.GeminiImageModel client makeRequest ct
                    match result with
                    | Ok body ->
                        match GeminiWire.tryParseGenerateContentResponse body with
                        | Ok(parts, usage) ->
                            match parts |> List.tryFind (fun (_, mime) -> mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) with
                            | Some(bytes, _) ->
                                call.Succeeded(usage)
                                return Ok(bytes, usage)
                            | None ->
                                logger.LogWarning(
                                    "Gemini image response had no image/* inlineData part: model={Model} status=200 body={Body}",
                                    conf.GeminiImageModel, body)
                                let err = LlmError.ApiError(200, body)
                                call.Failed(err)
                                return Error err
                        | Error parseError when parseError.StartsWith("blocked:") ->
                            logger.LogWarning(
                                "Gemini image request blocked: model={Model} status=200 reason={Reason} body={Body}",
                                conf.GeminiImageModel, parseError, body)
                            let err = LlmError.ContentFiltered parseError
                            call.Failed(err)
                            return Error err
                        | Error parseError ->
                            logger.LogError("Unparseable Gemini image response ({Error}): {Body}", parseError, body)
                            let err = LlmError.ApiError(200, body)
                            call.Failed(err)
                            return Error err
                    | Error err ->
                        GeminiWire.logError logger conf.GeminiImageModel err
                        call.Failed(err)
                        return Error err
            }

/// GeminiMusicGen against Google's generateContent endpoint — Lyria. `prompt` is the
/// caller-assembled style + lyrics text (BotService's `/song`). Returns whatever audio
/// container the response's inlineData carries (see GeminiWire's doc comment — unverified,
/// likely WAV/PCM per the task brief); BotService re-encodes to a Telegram-friendly format
/// the same way `/say`'s ISpeech.Synthesize output does, falling back to the raw bytes if
/// that fails.
type GeminiMusicGen(httpFactory: IHttpClientFactory, options: IOptions<BotConfiguration>, usageRecorder: IUsageRecorder, logger: ILogger<GeminiMusicGen>) =

    interface IMusicGen with
        member _.Generate(prompt: string, ctx: UsageContext, ct: CancellationToken) =
            task {
                let conf = options.Value
                if String.IsNullOrWhiteSpace conf.GeminiApiKey then
                    logger.LogWarning("Gemini music generation requested but GEMINI_API_KEY is unset")
                    return Error(LlmError.ApiError(0, "GEMINI_API_KEY not configured"))
                else
                    use call = new MusicCall(conf.GeminiMusicModel, conf.LlmPricingJson, usageRecorder, ctx, logger)
                    let client = httpFactory.CreateClient(Gemini.HttpClientName)
                    let uri = GeminiWire.generateContentUri conf.GeminiBaseUrl conf.GeminiMusicModel
                    let bodyJson = GeminiWire.buildMusicGenBody prompt
                    let makeRequest () = GeminiWire.newRequest conf.GeminiApiKey uri bodyJson

                    let! result, _retries = GeminiHttp.sendWithRetry logger conf.GeminiMusicModel client makeRequest ct
                    match result with
                    | Ok body ->
                        match GeminiWire.tryParseGenerateContentResponse body with
                        | Ok(parts, usage) ->
                            match parts |> List.tryFind (fun (_, mime) -> mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) with
                            | Some(bytes, _) ->
                                call.Succeeded(usage)
                                return Ok(bytes, usage)
                            | None ->
                                // No audio/* mime found — fall back to the first inlineData
                                // part regardless of its declared mime (best-effort: the
                                // real response shape is unverified, see module doc comment).
                                match parts with
                                | (bytes, _) :: _ ->
                                    call.Succeeded(usage)
                                    return Ok(bytes, usage)
                                | [] ->
                                    logger.LogWarning(
                                        "Gemini music response had no inlineData parts: model={Model} status=200 body={Body}",
                                        conf.GeminiMusicModel, body)
                                    let err = LlmError.ApiError(200, body)
                                    call.Failed(err)
                                    return Error err
                        | Error parseError when parseError.StartsWith("blocked:") ->
                            logger.LogWarning(
                                "Gemini music request blocked: model={Model} status=200 reason={Reason} body={Body}",
                                conf.GeminiMusicModel, parseError, body)
                            let err = LlmError.ContentFiltered parseError
                            call.Failed(err)
                            return Error err
                        | Error parseError ->
                            logger.LogError("Unparseable Gemini music response ({Error}): {Body}", parseError, body)
                            let err = LlmError.ApiError(200, body)
                            call.Failed(err)
                            return Error err
                    | Error err ->
                        GeminiWire.logError logger conf.GeminiMusicModel err
                        call.Failed(err)
                        return Error err
            }

/// Single IImageGen registration (Program.fs) dispatching to Azure or Gemini per-call by
/// the IMAGE_PROVIDER bot_setting ("azure" | "gemini", default "gemini" — see
/// BotConfiguration.ImageProvider's doc comment) — reads `options.Value` fresh every call,
/// so `/reload-settings` flips providers with no pod restart, same hot-reload guarantee
/// every other bot_setting gets.
type ImageGenRouter(azure: AzureFoundryImageGen, gemini: GeminiImageGen, options: IOptions<BotConfiguration>) =
    interface IImageGen with
        member _.Generate(prompt: string, sourceImage: byte[] option, ctx: UsageContext, ct: CancellationToken) =
            let inner: IImageGen =
                if options.Value.ImageProvider.Equals("azure", StringComparison.OrdinalIgnoreCase) then
                    azure :> IImageGen
                else
                    gemini :> IImageGen
            inner.Generate(prompt, sourceImage, ctx, ct)
