namespace AlitaBot.Services

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open AlitaBot

type IEmbeddingService =
    abstract member Embed: text: string -> Task<float32[] option>

type AzureEmbeddingService(httpClient: HttpClient, botConf: BotConfiguration, logger: ILogger<AzureEmbeddingService>) =

    let parseResponse (json: string) : float32[] option =
        try
            use doc = JsonDocument.Parse(json)
            let values =
                doc.RootElement
                   .GetProperty("data").[0]
                   .GetProperty("embedding")
                   .EnumerateArray()
                |> Seq.map (fun e -> e.GetSingle())
                |> Seq.toArray
            Some values
        with ex ->
            logger.LogWarning(ex, "Failed to parse embedding response")
            None

    interface IEmbeddingService with
        member _.Embed(text: string) = task {
            let url =
                $"{botConf.AzureFoundryEndpoint}/openai/deployments/{botConf.EmbeddingDeployment}/embeddings?api-version=2024-08-01-preview"

            let requestBody =
                JsonSerializer.Serialize({| input = text |})

            use request = new HttpRequestMessage(HttpMethod.Post, url)
            request.Headers.Add("api-key", botConf.AzureFoundryKey)
            request.Content <- new StringContent(requestBody, Encoding.UTF8, "application/json")

            try
                use! response = httpClient.SendAsync(request)
                let! body = response.Content.ReadAsStringAsync()

                if response.IsSuccessStatusCode then
                    return parseResponse body
                else
                    logger.LogWarning("Embedding API returned {Status}: {Body}", int response.StatusCode, body)
                    return None
            with ex ->
                logger.LogWarning(ex, "Embedding API call failed")
                return None
        }
