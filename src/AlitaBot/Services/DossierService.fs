namespace AlitaBot.Services

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open AlitaBot

type DossierService(
    db: DbService,
    embedding: IEmbeddingService,
    httpClient: HttpClient,
    botConf: BotConfiguration,
    logger: ILogger<DossierService>
) =

    // ── LLM helpers ─────────────────────────────────────────────────────────

    let callLlm (systemPrompt: string) (userPrompt: string) : Task<string option> = task {
        let url =
            $"{botConf.AzureFoundryEndpoint}/openai/deployments/{botConf.ResponderDeployment}/chat/completions?api-version=2024-08-01-preview"

        let requestBody =
            JsonSerializer.Serialize(
                {| messages =
                    [| {| role = "system"; content = systemPrompt |}
                       {| role = "user";   content = userPrompt   |} |]
                   max_tokens  = 1000
                   temperature = 0.3 |})

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
                    return Some content
                with ex ->
                    logger.LogWarning(ex, "Failed to parse LLM response in DossierService")
                    return None
            else
                logger.LogWarning("LLM call in DossierService returned {Status}", int response.StatusCode)
                return None
        with ex ->
            logger.LogWarning(ex, "LLM call in DossierService failed")
            return None
    }

    // ── Per-message ──────────────────────────────────────────────────────────

    /// Log the message and upsert person record. Called for every qualifying message.
    member _.LogMessage(userId: int64, chatId: int64, username: string, displayName: string, text: string) : Task =
        task {
            do! db.LogMessage(userId, chatId, text)
            do! db.UpsertPerson(userId, username, displayName)
        }

    /// Load dossier + semantically recalled memories for a user.
    /// Returns (summaryOrEmpty, recalledFacts).
    member _.LoadContext(userId: int64, messageText: string) : Task<string * string array> = task {
        let! dossier = db.GetDossier(userId)
        let summary = dossier |> Option.bind (fun d -> Option.ofObj d.Summary) |> Option.defaultValue ""

        let! embeddingOpt = embedding.Embed(messageText)
        let! memories =
            match embeddingOpt with
            | Some vec -> db.RecallMemories(userId, vec, botConf.TopMemoriesPerUser)
            | None     -> Task.FromResult [||]

        return summary, memories
    }

    // ── Daily update job ─────────────────────────────────────────────────────

    /// For each user active in the last 24h:
    ///   1. Extract new facts from yesterday's messages (LLM)
    ///   2. Embed each fact → insert to interaction_memory
    ///   3. Produce updated cumulative summary (LLM) → update person_dossier
    member this.RunDailyUpdate() : Task = task {
        let! activeUsers = db.GetActiveUsersLastDay()
        logger.LogInformation("DossierService: updating {Count} users", activeUsers.Length)

        for userId in activeUsers do
            try
                let! messages = db.GetUserMessagesLastDay(userId)
                if messages.Length > 0 then
                    let! dossier = db.GetDossier(userId)
                    let existingSummary = dossier |> Option.bind (fun d -> Option.ofObj d.Summary) |> Option.defaultValue "(no prior summary)"

                    let messagesText = messages |> String.concat "\n"

                    // Step 1: Extract new facts
                    let extractSystemPrompt =
                        """You analyse recent Telegram messages from one person and extract new facts not already present in their existing summary.
Facts should cover: personality traits, opinions, likes/dislikes, hobbies, relationships with others, recurring topics, notable quotes.
Return ONLY a JSON array of short strings, each a distinct new fact. If no new facts, return [].
Example: ["Likes cats", "Mentioned hating Mondays", "Studies at MSU"]"""

                    let extractUserPrompt =
                        $"Existing summary:\n{existingSummary}\n\nRecent messages:\n{messagesText}"

                    let! factsJsonOpt = callLlm extractSystemPrompt extractUserPrompt

                    let newFacts =
                        match factsJsonOpt with
                        | None -> [||]
                        | Some json ->
                            try
                                JsonSerializer.Deserialize<string[]>(json)
                            with ex ->
                                logger.LogWarning(ex, "Could not parse facts JSON for user {UserId}: {Json}", userId, json)
                                [||]

                    // Step 2: Embed and store each new fact
                    for fact in newFacts do
                        let! embOpt = embedding.Embed(fact)
                        match embOpt with
                        | Some vec -> do! db.InsertMemory(userId, fact, vec)
                        | None     -> ()   // log already done in EmbeddingService

                    // Step 3: Update cumulative summary
                    if newFacts.Length > 0 then
                        let newFactsText = newFacts |> String.concat "\n- "
                        let summarySystemPrompt =
                            """You maintain a growing personal profile for a member of a private Telegram group.
Merge the existing summary with the new facts into an updated, coherent paragraph.
Do not remove or contradict existing information unless clearly superseded.
Be concise (max 250 words). Write in English."""

                        let summaryUserPrompt =
                            $"Existing summary:\n{existingSummary}\n\nNew facts to incorporate:\n- {newFactsText}"

                        let! newSummaryOpt = callLlm summarySystemPrompt summaryUserPrompt
                        match newSummaryOpt with
                        | Some newSummary -> do! db.UpdateDossierSummary(userId, newSummary)
                        | None            -> ()

                    Metrics.dossierUpdatesTotal.Add(1L)
            with ex ->
                logger.LogError(ex, "DossierService: failed to update user {UserId}", userId)
    }
