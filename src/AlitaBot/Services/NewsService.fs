namespace AlitaBot.Services

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open AlitaBot

type NewsService(
    db: DbService,
    httpClient: HttpClient,
    botConf: BotConfiguration,
    logger: ILogger<NewsService>
) =

    let summariseWithLlm (pageContent: string) (sourceUrl: string) : Task<string option> = task {
        let url =
            $"{botConf.AzureFoundryEndpoint}/openai/deployments/{botConf.ResponderDeployment}/chat/completions?api-version=2024-08-01-preview"

        let systemPrompt =
            """You are a news summariser for a private Russian-speaking Telegram community.
Read the page content and extract the most interesting or surprising news of the day.
Write a short, punchy summary in Russian using casual conversational language, as if you are sharing news with friends.
Max 3-4 sentences. Start directly — no intro like "На странице...".
If there is nothing interesting or the content is not news, return exactly: SKIP"""

        let truncated =
            if pageContent.Length > 8000 then pageContent.[..7999] + "..."
            else pageContent

        let requestBody =
            JsonSerializer.Serialize(
                {| messages =
                    [| {| role = "system"; content = systemPrompt |}
                       {| role = "user";   content = $"Source: {sourceUrl}\n\n{truncated}" |} |]
                   max_tokens  = 300
                   temperature = 0.7 |})

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
                    if content.Trim() = "SKIP" then return None
                    else return Some (content.Trim())
                with ex ->
                    logger.LogWarning(ex, "Failed to parse news LLM response for {Url}", sourceUrl)
                    return None
            else
                logger.LogWarning("News LLM returned {Status} for {Url}", int response.StatusCode, sourceUrl)
                return None
        with ex ->
            logger.LogWarning(ex, "News LLM call failed for {Url}", sourceUrl)
            return None
    }

    /// Fetch each configured news source, summarise with LLM, store result.
    /// Errors per source are logged and skipped (best-effort).
    member _.FetchAndSummarise() : Task<int> = task {
        let mutable inserted = 0

        for sourceUrl in botConf.NewsSourceUrls do
            try
                use pageRequest = new HttpRequestMessage(HttpMethod.Get, sourceUrl)
                pageRequest.Headers.Add("User-Agent", "AlitaBot/1.0")

                use! pageResponse = httpClient.SendAsync(pageRequest)
                let! pageContent = pageResponse.Content.ReadAsStringAsync()

                let! summaryOpt = summariseWithLlm pageContent sourceUrl
                match summaryOpt with
                | Some summary ->
                    do! db.InsertNewsSummary(sourceUrl, summary)
                    inserted <- inserted + 1
                    logger.LogInformation("News: summarised {Url}", sourceUrl)
                | None ->
                    logger.LogInformation("News: nothing interesting at {Url}", sourceUrl)
            with ex ->
                logger.LogWarning(ex, "News: failed to fetch/summarise {Url}", sourceUrl)

        return inserted
    }
