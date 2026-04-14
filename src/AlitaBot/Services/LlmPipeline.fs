namespace AlitaBot.Services

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open AlitaBot

// ── Layer marker interfaces ─────────────────────────────────────────────────

type ILlmLayer1 =
    abstract member Call: systemPrompt: string * messages: (string * string) list -> Task<string option>

type ILlmLayer2 =
    abstract member Call: systemPrompt: string * messages: (string * string) list -> Task<string option>

type ILlmLayer3 =
    abstract member Call: systemPrompt: string * messages: (string * string) list -> Task<string option>

// ── Shared Azure Foundry HTTP implementation ────────────────────────────────

/// One concrete class used for all three layer interfaces;
/// each gets its own HttpClient registration with the right deployment baked in via BotConfiguration.
type AzureFoundryLlm(httpClient: HttpClient, botConf: BotConfiguration, logger: ILogger<AzureFoundryLlm>, deployment: string) =

    let call (systemPrompt: string) (messages: (string * string) list) (maxTokens: int) (temperature: float) : Task<string option> = task {
        let url =
            $"{botConf.AzureFoundryEndpoint}/openai/deployments/{deployment}/chat/completions?api-version=2024-08-01-preview"

        let msgObjects =
            [| yield {| role = "system"; content = systemPrompt |}
               for (role, content) in messages do
                   yield {| role = role; content = content |} |]

        let requestBody =
            JsonSerializer.Serialize(
                {| messages    = msgObjects
                   max_tokens  = maxTokens
                   temperature = temperature |})

        use request = new HttpRequestMessage(HttpMethod.Post, url)
        request.Headers.Add("api-key", botConf.AzureFoundryKey)
        request.Content <- new StringContent(requestBody, Encoding.UTF8, "application/json")

        try
            use! response = httpClient.SendAsync(request)
            let! body = response.Content.ReadAsStringAsync()

            if response.IsSuccessStatusCode then
                try
                    use doc = JsonDocument.Parse(body)
                    let content =
                        doc.RootElement
                           .GetProperty("choices").[0]
                           .GetProperty("message")
                           .GetProperty("content")
                           .GetString()
                    return Some (content.Trim())
                with ex ->
                    logger.LogWarning(ex, "Failed to parse LLM response from {Deployment}", deployment)
                    return None
            else
                logger.LogWarning("LLM {Deployment} returned {Status}", deployment, int response.StatusCode)
                return None
        with ex ->
            logger.LogWarning(ex, "LLM {Deployment} call failed", deployment)
            return None
    }

    // Expose via all three interfaces
    interface ILlmLayer1 with
        member _.Call(sys, msgs) = call sys msgs 600 0.8

    interface ILlmLayer2 with
        member _.Call(sys, msgs) = call sys msgs 600 0.7

    interface ILlmLayer3 with
        member _.Call(sys, msgs) = call sys msgs 200 1.0

// ── Layer 3 outcome ─────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
type Layer3Outcome =
    | Silence
    | Usual
    | EmojiMeme
    | Chaos

// ── Main pipeline ────────────────────────────────────────────────────────────

type LlmPipeline(
    layer1: ILlmLayer1,
    layer2: ILlmLayer2,
    layer3: ILlmLayer3,
    dossier: DossierService,
    options: IOptions<BotConfiguration>,
    logger: ILogger<LlmPipeline>
) =
    let rng = Random.Shared

    let pickOutcome () : Layer3Outcome =
        let conf = options.Value
        let weights =
            [ Layer3Outcome.Silence,   conf.Layer3WeightSilence
              Layer3Outcome.Usual,     conf.Layer3WeightUsual
              Layer3Outcome.EmojiMeme, conf.Layer3WeightEmojiMeme
              Layer3Outcome.Chaos,     conf.Layer3WeightChaos ]
        let total = weights |> List.sumBy snd
        let roll  = rng.Next(0, total)
        let mutable cumulative = 0
        let mutable result = Layer3Outcome.Silence
        for (outcome, weight) in weights do
            cumulative <- cumulative + weight
            if roll < cumulative && result = Layer3Outcome.Silence && outcome <> Layer3Outcome.Silence then
                result <- outcome
            elif roll < cumulative && result = Layer3Outcome.Silence && outcome = Layer3Outcome.Silence then
                result <- Layer3Outcome.Silence
        // walk through properly
        let mutable picked = Layer3Outcome.Silence
        let mutable found  = false
        let mutable acc    = 0
        for (outcome, weight) in weights do
            if not found then
                acc <- acc + weight
                if roll < acc then
                    picked <- outcome
                    found  <- true
        picked

    /// Full 3-layer pipeline.
    /// Returns None if Layer 3 decides to stay silent.
    member _.Generate(
            userId:      int64,
            username:    string,
            messageText: string,
            chatContext: string list) : Task<string option> = task {

        let botConf = options.Value

        // Load dossier + recalled memories
        let! (summary, memories) = dossier.LoadContext(userId, messageText)

        // Build Layer 1 system prompt
        let memoriesSection =
            if memories.Length = 0 then ""
            else
                let bullets = memories |> Array.map (fun m -> $"- {m}") |> String.concat "\n"
                $"\n\nWhat you recall about this person:\n{bullets}"

        let dossierSection =
            if String.IsNullOrWhiteSpace summary then ""
            else $"\n\nPerson profile:\n{summary}"

        let l1System =
            $"""You are {botConf.BotUsername}, a member of a small private Telegram community.
You are warm, curious, occasionally sarcastic, and speak like a real person (not an assistant).
Respond naturally to the message below. Be concise (1-4 sentences).{dossierSection}{memoriesSection}"""

        let contextMessages =
            [ for msg in chatContext do yield ("user", msg)
              yield ("user", messageText) ]

        // Layer 1: generate raw response
        let! l1ResultOpt = layer1.Call(l1System, contextMessages)
        match l1ResultOpt with
        | None ->
            logger.LogWarning("Layer 1 returned no response for user {UserId}", userId)
            return None
        | Some l1Response ->

        // Layer 2: rewrite with persona voice
        let l2System =
            $"""You are an editor polishing a response from {botConf.BotUsername} to make it sound more natural and human.
Keep the meaning but adjust tone: casual, slightly informal, with personality.
Never sound like an AI assistant. Remove phrases like "Great question!" or "Certainly!".
Return ONLY the polished response text, nothing else."""

        let! l2ResultOpt = layer2.Call(l2System, [("user", l1Response)])
        let l2Response = l2ResultOpt |> Option.defaultValue l1Response

        // Layer 3: weighted coin flip
        let outcome = pickOutcome()
        Metrics.messagesProcessed.Add(1L, System.Collections.Generic.KeyValuePair("outcome", box outcome))

        match outcome with
        | Layer3Outcome.Silence ->
            logger.LogDebug("Layer 3: silence for user {UserId}", userId)
            return None

        | Layer3Outcome.Usual ->
            return Some l2Response

        | Layer3Outcome.EmojiMeme ->
            let l3System =
                $"""You are {botConf.BotUsername}. React to this conversation with only 1-3 emoji or a very short meme reference (max 5 words).
No full sentences. Pure reaction. Return ONLY the emoji/meme text."""
            let! l3ResultOpt = layer3.Call(l3System, [("user", messageText)])
            return l3ResultOpt |> Option.orElse (Some "🤔")

        | Layer3Outcome.Chaos ->
            let l3System =
                $"""You are {botConf.BotUsername}. You suddenly remembered something completely unrelated.
Ignore the conversation topic entirely and bring up something random that just crossed your mind — a memory, a thought, a weird question.
Stay in character. Keep it short (1-2 sentences). Return ONLY the message text."""
            let! l3ResultOpt = layer3.Call(l3System, [("user", messageText)])
            return l3ResultOpt |> Option.orElse (Some "Кстати, а вы замечали как странно работает интуиция?")
    }

    /// Runs the full pipeline with a standalone prompt (no specific sender, for proactive posts).
    member _.GenerateProactive(prompt: string) : Task<string option> = task {
        let botConf = options.Value
        let l1System =
            $"""You are {botConf.BotUsername}, a member of a small private Telegram community.
You are about to start a new conversation or share something you just thought of.
Be natural, casual, and engaging. 1-3 sentences max."""

        let! l1Opt = layer1.Call(l1System, [("user", prompt)])
        match l1Opt with
        | None -> return None
        | Some l1 ->

        let l2System =
            $"""Polish this message from {botConf.BotUsername} to sound more natural and human.
Return ONLY the polished text."""
        let! l2Opt = layer2.Call(l2System, [("user", l1)])
        return l2Opt |> Option.orElse (Some l1)
    }
