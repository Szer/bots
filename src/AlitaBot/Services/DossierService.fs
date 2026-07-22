namespace AlitaBot.Services

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open AlitaBot
open AlitaBot.Llm
open AlitaBot.Telemetry
open BotInfra

/// Slice 5b: nightly per-person fact extraction + dossier summary merge. Driven by
/// `SchedulerHostedService` (ScheduledJobs.fs), 02:00 UTC, one lease-locked pass over
/// every user active in the last 24h and not opted out (`memory_opt_out`).
///
/// Fixes over the old (pre-Funogram) `feature/alita-bot` branch's `DossierService.fs`
/// design (see `git show origin/feature/alita-bot:src/AlitaBot/Services/DossierService.fs`):
/// no similarity floor on recall (fixed by ResponderService's DOSSIER_SIM_FLOOR), no fact
/// dedup (fixed here — `DedupSimFloor`), ivfflat-on-an-empty-table (fixed by the V4
/// migration's HNSW index).
type DossierService
    (
        db: DbService,
        chat: IChatCompletion,
        embeddings: IEmbeddings,
        options: IOptions<BotConfiguration>,
        logger: ILogger<DossierService>,
        time: TimeProvider
    ) =

    /// A candidate fact within this cosine similarity of an existing ACTIVE fact for the
    /// same user is treated as a duplicate and never inserted — see `ProcessUser`.
    [<Literal>]
    let DedupSimFloor = 0.90

    let buildTranscript (rows: MessageLogRow[]) : string =
        rows |> Array.map (fun r -> $"[{r.display_name}]: {r.text}") |> String.concat "\n"

    /// Lenient parse of the extraction LLM's JSON-array-of-strings response — same
    /// tolerant style as BotService.parseModelAllowlist. Malformed JSON, a non-array
    /// value, or non-string elements are all skipped rather than failing the whole run;
    /// blank/whitespace-only strings are dropped too.
    let parseFactsJson (json: string) : string list =
        try
            use doc = JsonDocument.Parse(json: string)
            if doc.RootElement.ValueKind <> JsonValueKind.Array then
                []
            else
                [ for el in doc.RootElement.EnumerateArray() do
                    if el.ValueKind = JsonValueKind.String then
                        let s = el.GetString()
                        if not (String.IsNullOrWhiteSpace s) then s.Trim() ]
        with _ -> []

    let extractRequest (conf: BotConfiguration) (existingSummary: string) (transcript: string) : ChatRequest =
        { Deployment = conf.LlmDeployment
          Messages =
            [ { Role = ChatRole.System
                Content = [ ContentPart.Text conf.ExtractPrompt ]
                ToolCalls = []
                ToolCallId = None }
              { Role = ChatRole.User
                Content =
                  [ ContentPart.Text
                        $"Текущее досье:\n{existingSummary}\n\nСообщения этого человека за последние 24 часа:\n{transcript}" ]
                ToolCalls = []
                ToolCallId = None } ]
          Tools = []
          // gpt-5-mini (a reasoning model) rejects any non-default temperature outright
          // ("Unsupported value: 'temperature' does not support 0.2 with this model. Only
          // the default (1) value is supported.") — confirmed against the real deployment
          // (DossierRealTests). Every other ChatRequest in this codebase already omits it
          // for the same reason (AzureFoundryProvider.fs's buildChatBody comment); this one
          // originally set Some 0.2 for "low-temperature, more deterministic extraction",
          // which the fake suite's LLM never validates — only a real call caught it.
          Temperature = None
          MaxTokens = None }

    let mergeRequest (conf: BotConfiguration) (existingSummary: string) (newFacts: string list) : ChatRequest =
        let factsText = newFacts |> List.map (fun f -> $"- {f}") |> String.concat "\n"
        { Deployment = conf.LlmDeployment
          Messages =
            [ { Role = ChatRole.System
                Content = [ ContentPart.Text conf.MergePrompt ]
                ToolCalls = []
                ToolCallId = None }
              { Role = ChatRole.User
                Content = [ ContentPart.Text $"Текущее досье:\n{existingSummary}\n\nНовые факты:\n{factsText}" ]
                ToolCalls = []
                ToolCallId = None } ]
          Tools = []
          Temperature = None
          MaxTokens = None }

    /// One user's nightly pass: extract candidate facts (LLM), embed + dedup + insert each
    /// one, then — only if at least one fact was actually new — merge the summary (LLM)
    /// and upsert `person_dossier`. Best-effort throughout: an LLM/embedding failure for
    /// this user is Warning-logged and the method just returns, never throws (the caller,
    /// `RunNightlyUpdate`, additionally isolates per-user exceptions so one bad response
    /// can't abort the whole run).
    member _.ProcessUser(userId: int64, displayName: string, existingSummary: string, rows: MessageLogRow[]) : Task<unit> =
        task {
            let conf = options.Value
            let usageCtx: UsageContext = { ChatId = None; UserId = Some userId }
            let transcript = buildTranscript rows

            use extractSpan = botActivity.StartActivity("dossier.extract")
            %extractSpan.SetTag("userId", userId)

            match! chat.Complete(extractRequest conf existingSummary transcript, usageCtx, CancellationToken.None) with
            | Error err ->
                %extractSpan.SetTag("outcome", "extract_failed")
                logger.LogWarning("Dossier fact extraction failed for user {UserId}: {Error}", userId, string err)
            | Ok resp ->
                let candidateFacts = parseFactsJson resp.Text
                %extractSpan.SetTag("candidateFacts", candidateFacts.Length)

                let mutable insertedFacts: string list = []

                for fact in candidateFacts do
                    match! embeddings.Embed(conf.EmbeddingDeployment, [ fact ], usageCtx, CancellationToken.None) with
                    | Error err ->
                        logger.LogWarning("Dossier: failed to embed a candidate fact for user {UserId}: {Error}", userId, string err)
                    | Ok vectors when vectors.Length = 0 || vectors[0].Length = 0 -> ()
                    | Ok vectors ->
                        let! bestSim = db.NearestActiveFactSimilarity(userId, vectors[0])
                        let isDuplicate = bestSim |> Option.exists (fun s -> s >= DedupSimFloor)
                        if isDuplicate then
                            ()
                        else
                            do! db.InsertInteractionMemory(userId, fact, vectors[0])
                            insertedFacts <- fact :: insertedFacts

                %extractSpan.SetTag("insertedFacts", insertedFacts.Length)

                if not insertedFacts.IsEmpty then
                    use mergeSpan = botActivity.StartActivity("dossier.merge")
                    %mergeSpan.SetTag("userId", userId)
                    %mergeSpan.SetTag("newFacts", insertedFacts.Length)

                    match! chat.Complete(mergeRequest conf existingSummary insertedFacts, usageCtx, CancellationToken.None) with
                    | Ok mergeResp when not (String.IsNullOrWhiteSpace mergeResp.Text) ->
                        do! db.UpsertPersonDossier(userId, displayName, mergeResp.Text.Trim())
                        %mergeSpan.SetTag("outcome", "merged")
                    | Ok _ -> %mergeSpan.SetTag("outcome", "empty_response")
                    | Error err ->
                        %mergeSpan.SetTag("outcome", "merge_failed")
                        logger.LogWarning("Dossier summary merge failed for user {UserId}: {Error}", userId, string err)
        }

    /// Nightly (02:00 UTC, `SchedulerHostedService`) fact-extraction + summary-merge pass
    /// over every user active in the last 24h and not opted out. Per-user failures
    /// (unexpected exceptions — `ProcessUser` itself already handles LLM/embedding errors
    /// gracefully) are caught and logged so one user can't abort the whole run.
    member this.RunNightlyUpdate() : Task<unit> =
        task {
            let since = time.GetUtcNow().UtcDateTime.AddHours(-24.0)
            let! userIds = db.ActiveUsersLast24h(since)
            logger.LogInformation("DossierService: nightly update for {Count} active user(s)", userIds.Length)

            for userId in userIds do
                try
                    let! rows = db.UserMessagesLast24h(userId, since)
                    if rows.Length > 0 then
                        let! dossierOpt = db.GetPersonDossier(userId)
                        let existingSummary =
                            match dossierOpt with
                            | Some d when not (String.IsNullOrWhiteSpace d.summary) -> d.summary
                            | _ -> "(пока ничего не известно)"
                        let displayName = rows[rows.Length - 1].display_name
                        do! this.ProcessUser(userId, displayName, existingSummary, rows)
                with ex ->
                    logger.LogError(ex, "DossierService: failed to update user {UserId}", userId)
        }
