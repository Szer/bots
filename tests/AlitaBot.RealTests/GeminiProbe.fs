/// Pre-flight capability probe for the Gemini real tests (ImageGenRealTests/SongRealTests).
///
/// DISCOVERY (2026-07-22, see PR description / GeminiProvider.fs's doc comment for the full
/// transcript): this repo's ALITA_GEMINI_API_KEY works fine for TEXT generateContent calls,
/// but every image/music-capable model 429s with a Google Cloud billing gate specific to
/// this key's project — `RESOURCE_EXHAUSTED`, `free_tier_requests, limit: 0` — confirmed
/// NOT a transient rate limit (a malformed request body still 400s before quota is even
/// consulted; text models on the same key 200 fine). Real success was never observed.
///
/// Rather than let that external, pre-existing billing gate hard-fail `make real-test`
/// forever, these real tests probe the SAME failure mode directly (one real HTTP call) and
/// self-skip with a clear diagnostic when it's hit — mirroring ImageGenRealTests' existing
/// self-skip for Azure's 0 image quota (AlitaBot/docs/TECH-DEBT.md). A genuine regression
/// (any OTHER failure, or no failure at all) still runs the full Telegram round-trip
/// assertion and fails loudly like normal.
module AlitaBot.RealTests.GeminiProbe

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks

/// True when `model` 429s with Google's free-tier-limit-0 billing gate for THIS key —
/// i.e. real generation against it cannot succeed no matter how correct the bot's request
/// is. Any other outcome (200, a different error, a network failure) returns false — those
/// are either fine to proceed on, or a real bug that should surface as a normal test
/// failure rather than a silent skip.
let isQuotaBlocked (apiKey: string) (model: string) : Task<bool> =
    task {
        try
            use http = new HttpClient(Timeout = TimeSpan.FromSeconds 15.)
            use req =
                new HttpRequestMessage(
                    HttpMethod.Post,
                    $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent")
            req.Headers.Add("x-goog-api-key", apiKey)
            req.Content <-
                new StringContent(
                    """{"contents":[{"parts":[{"text":"capability probe"}]}]}""",
                    Encoding.UTF8,
                    "application/json")
            use! resp = http.SendAsync req
            if resp.StatusCode <> Net.HttpStatusCode.TooManyRequests then
                return false
            else
                let! body = resp.Content.ReadAsStringAsync()
                try
                    use doc = JsonDocument.Parse body
                    match doc.RootElement.TryGetProperty "error" with
                    | true, err ->
                        match err.TryGetProperty "message" with
                        | true, m when m.ValueKind = JsonValueKind.String ->
                            let msg = m.GetString()
                            return msg.Contains("free_tier", StringComparison.OrdinalIgnoreCase)
                                   && msg.Contains("limit: 0", StringComparison.OrdinalIgnoreCase)
                        | _ -> return false
                    | _ -> return false
                with _ ->
                    return false
        with _ ->
            return false
    }
