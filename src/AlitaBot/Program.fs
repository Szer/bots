// AlitaBot — conversational Telegram chatbot
open System
open System.Diagnostics
open System.Globalization
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Microsoft.Extensions.Time.Testing
open AlitaBot
open AlitaBot.Llm
open AlitaBot.Services
open AlitaBot.Telemetry
open BotInfra
open BotInfra.DbSettings

type Root = class end

let connString = getEnv "DATABASE_URL"

let parseChatIds (raw: string) =
    if String.IsNullOrWhiteSpace raw then
        []
    else
        raw.Split([| ','; ';'; ' ' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun s ->
            match Int64.TryParse(s.Trim()) with
            | true, v -> Some v
            | _ -> None)
        |> Array.toList

let loadDbSettings () =
    try
        DbSettings.loadBotSettings(connString).GetAwaiter().GetResult()
    with e ->
        eprintfn "[FATAL] Failed to load bot settings from database: %O" e
        reraise()

let mutable dbSettings = loadDbSettings()

let getSettingOr key def =
    let accessor = BotSettingsAccessor(dbSettings)
    accessor.GetSettingOr(key, def)

let buildBotConf () =
    { BotToken = getEnv "BOT_TELEGRAM_TOKEN"
      SecretToken = getEnv "BOT_AUTH_TOKEN"
      TelegramApiBaseUrl =
        match getEnvOr "TELEGRAM_API_URL" "" with
        | "" -> null
        | v -> v
      TargetChatIds = getSettingOr "TARGET_CHAT_IDS" (getEnvOr "TARGET_CHAT_IDS" "") |> parseChatIds
      BotUsername = getSettingOr "BOT_USERNAME" (getEnvOr "BOT_USERNAME" "")
      SystemPrompt = getSettingOr "SYSTEM_PROMPT" ""
      AzureFoundryEndpoint = getSettingOr "AZURE_FOUNDRY_ENDPOINT" (getEnvOr "AZURE_FOUNDRY_ENDPOINT" "")
      AzureFoundryKey = getEnvOr "AZURE_FOUNDRY_KEY" ""
      LlmDeployment = getSettingOr "LLM_DEPLOYMENT" ""
      EmbeddingDeployment = getSettingOr "EMBEDDING_DEPLOYMENT" ""
      SttDeployment = getSettingOr "STT_DEPLOYMENT" ""
      TtsDeployment = getSettingOr "TTS_DEPLOYMENT" ""
      LlmPricingJson = getSettingOr "LLM_PRICING" "{}"
      ResponderMode = getSettingOr "RESPONDER_MODE" "echo"
      StreamMode = getSettingOr "STREAM_MODE" "edit"
      ContextWindowMessages = getSettingOr "CONTEXT_WINDOW_MESSAGES" "30" |> int
      VoiceTranscribeEnabled = getSettingOr "VOICE_TRANSCRIBE_ENABLED" "true" |> bool.Parse
      VisionEnabled = getSettingOr "VISION_ENABLED" "true" |> bool.Parse
      VisionDetail = getSettingOr "VISION_DETAIL" "low"
      ImageDeployment = getSettingOr "IMAGE_DEPLOYMENT" (getEnvOr "IMAGE_DEPLOYMENT" "")
      ImageGenEnabled = getSettingOr "IMAGE_GEN_ENABLED" "true" |> bool.Parse
      ImageSize = getSettingOr "IMAGE_SIZE" "1024x1024"
      ImageQuality = getSettingOr "IMAGE_QUALITY" "medium"
      LlmModelsJson = getSettingOr "LLM_MODELS" "[]"
      SummaryPrompt = getSettingOr "SUMMARY_PROMPT" ""
      EmbedMessagesEnabled = getSettingOr "EMBED_MESSAGES" "true" |> bool.Parse
      EmbeddingMinChars = getSettingOr "EMBEDDING_MIN_CHARS" "3" |> int
      AskTopK = getSettingOr "ASK_TOP_K" "8" |> int
      AskSimFloor = getSettingOr "ASK_SIM_FLOOR" "0.5" |> float
      AskPrompt = getSettingOr "ASK_PROMPT" ""
      DossierEnabled = getSettingOr "DOSSIER_ENABLED" "true" |> bool.Parse
      DossierRecallK = getSettingOr "DOSSIER_RECALL_K" "5" |> int
      DossierSimFloor = getSettingOr "DOSSIER_SIM_FLOOR" "0.60" |> float
      ExtractPrompt = getSettingOr "EXTRACT_PROMPT" ""
      MergePrompt = getSettingOr "MERGE_PROMPT" ""
      RewriterEnabled = getSettingOr "REWRITER_ENABLED" "false" |> bool.Parse
      RewriterPrompt = getSettingOr "REWRITER_PROMPT" ""
      OutcomeWeightsJson = getSettingOr "OUTCOME_WEIGHTS" """{"reply":100,"silence":0,"emoji":0}"""
      RoastPrompt = getSettingOr "ROAST_PROMPT" ""
      RoastCooldownSeconds = getSettingOr "ROAST_COOLDOWN_SECONDS" "300" |> int
      AwardsPrompt = getSettingOr "AWARDS_PROMPT" ""
      QuotePrompt = getSettingOr "QUOTE_PROMPT" ""
      // Slice 8: proactive behavior (morning digest, willingness-gated interjections,
      // meme reactions) — every default here keeps the bot polite/silent until hand-
      // enabled live via bot_setting (see dev-bot-settings.sql's header comment).
      DigestEnabled = getSettingOr "DIGEST_ENABLED" "false" |> bool.Parse
      DigestUtcHour = getSettingOr "DIGEST_UTC_HOUR" "7" |> int
      DigestMinMessages = getSettingOr "DIGEST_MIN_MESSAGES" "30" |> int
      DigestPrompt = getSettingOr "DIGEST_PROMPT" ""
      InterjectProbability = getSettingOr "INTERJECT_PROBABILITY" "0.0" |> float
      BurstMsgs = getSettingOr "BURST_MSGS" "8" |> int
      BurstSpeakers = getSettingOr "BURST_SPEAKERS" "3" |> int
      BurstWindowMinutes = getSettingOr "BURST_WINDOW_MINUTES" "5" |> int
      InterjectCooldownMinutes = getSettingOr "INTERJECT_COOLDOWN_MINUTES" "30" |> int
      InterjectPrompt = getSettingOr "INTERJECT_PROMPT" ""
      MemeReactProbability = getSettingOr "MEME_REACT_PROBABILITY" "0.0" |> float
      MemeReactPrompt = getSettingOr "MEME_REACT_PROMPT" ""
      // Slice 9 (stretch): /say voice replies, admin-gated /sql analytics, cost footer.
      TtsDefaultVoice = getSettingOr "TTS_DEFAULT_VOICE" "alloy"
      SayMaxChars = getSettingOr "SAY_MAX_CHARS" "500" |> int
      AdminUserIdsJson = getSettingOr "ADMIN_USER_IDS" "[]"
      SqlPrompt = getSettingOr "SQL_PROMPT" ""
      CostFooterEnabled = getSettingOr "COST_FOOTER_ENABLED" "false" |> bool.Parse
      // Gemini provider slice: Nano Banana images (an alternative /img backend to Azure,
      // routed by IMAGE_PROVIDER) + Lyria music (/song). GeminiApiKey is a secret (env,
      // like AzureFoundryKey) — everything else is a hot-reloadable bot_setting.
      GeminiApiKey = getEnvOr "GEMINI_API_KEY" ""
      GeminiBaseUrl = getSettingOr "GEMINI_BASE_URL" "https://generativelanguage.googleapis.com"
      GeminiImageModel = getSettingOr "GEMINI_IMAGE_MODEL" "gemini-3.1-flash-image"
      GeminiMusicModel = getSettingOr "GEMINI_MUSIC_MODEL" "lyria-3-pro-preview"
      ImageProvider = getSettingOr "IMAGE_PROVIDER" "gemini"
      SongMaxChars = getSettingOr "SONG_MAX_CHARS" "1000" |> int
      SongCooldownSeconds = getSettingOr "SONG_COOLDOWN_SECONDS" "120" |> int
      // S10 PR1: natural-language tool-calling loop (generate_image, web_search).
      NlToolsEnabled = getSettingOr "NL_TOOLS_ENABLED" "false" |> bool.Parse
      NlToolsMaxIterations = getSettingOr "NL_TOOLS_MAX_ITERATIONS" "4" |> int
      NlToolsRateLimitPerHour = getSettingOr "NL_TOOLS_RATE_LIMIT_PER_HOUR" "20" |> int
      ToolUsePrompt = getSettingOr "TOOL_USE_PROMPT" ""
      MediaCaptionPrompt = getSettingOr "MEDIA_CAPTION_PROMPT" ""
      WebSearchEnabled = getSettingOr "WEB_SEARCH_ENABLED" "true" |> bool.Parse
      AzureResponsesEndpoint =
        getSettingOr "AZURE_RESPONSES_ENDPOINT" (getEnvOr "AZURE_RESPONSES_ENDPOINT" "https://szer-foundry.openai.azure.com")
      WebSearchModel = getSettingOr "WEB_SEARCH_MODEL" ""
      TestMode = getSettingOr "TEST_MODE" (getEnvOr "TEST_MODE" "false") |> bool.Parse }

let botConfOptions = LiveOptions(buildBotConf())

let reloadSettings () =
    dbSettings <- loadDbSettings()
    botConfOptions.Set(buildBotConf())

let webhookCfg: WebhookConfig =
    let c = botConfOptions.Value
    { BotToken = c.BotToken
      SecretToken = c.SecretToken
      TelegramApiBaseUrl = c.TelegramApiBaseUrl
      OtelServiceName = "alita-bot"
      ActivitySourceName = botActivity.Name
      MeterName = "AlitaBot.Metrics"
      WebhookRoute = "/bot" }

let builder = WebApplication.CreateBuilder()

WebhookHost.configureSharedServices webhookCfg builder

%builder.Services.AddSingleton<IOptions<BotConfiguration>>(botConfOptions)

// In TestMode, replace the shared TimeProvider with a FakeTimeProvider so the
// /test/clock/advance endpoint can fire TimeProvider-driven timers deterministically.
// Initial time is taken from BOT_FIXED_UTC_NOW (same env var the production
// FixedTimeProvider reads), so time-sensitive tests keep fixed-clock semantics.
// Note: WebhookHost.configureSharedServices already registered TimeProvider.
// AddSingleton with the same service type overwrites for the last registration.
if botConfOptions.Value.TestMode then
    let initial =
        match Environment.GetEnvironmentVariable Time.FixedUtcNowEnvVar with
        | null | "" -> DateTimeOffset.UtcNow
        | raw ->
            DateTimeOffset.Parse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal)
    let fake = FakeTimeProvider(initial)
    %builder.Services.AddSingleton<FakeTimeProvider>(fake)
    %builder.Services.AddSingleton<TimeProvider>(fake :> TimeProvider)

// Named HttpClient for Azure Foundry LLM calls (raw HTTP + SSE, decision D3).
%builder.Services.AddHttpClient(AzureFoundry.HttpClientName)
// Named HttpClient for Gemini API calls (Nano Banana images, Lyria music) — same
// raw-HTTP idiom as Azure Foundry above, see Llm/GeminiProvider.fs.
%builder.Services.AddHttpClient(Gemini.HttpClientName)

%builder
    .Services
    .AddSingleton<DbService>(fun _ -> DbService(connString))
    // The llm_usage sink (IUsageRecorder) and the /reload-settings hook (ISettingsReloader)
    // are registered before the services that consume them — DI resolves lazily so
    // registration order doesn't strictly matter, but this keeps the dependency direction
    // readable: DbService owns both, BotService/the LLM provider layer just consume them.
    .AddSingleton<IUsageRecorder>(fun sp -> sp.GetRequiredService<DbService>() :> IUsageRecorder)
    .AddSingleton<ISettingsReloader>(
        { new ISettingsReloader with
            member _.Reload() = task { reloadSettings () } :> Task })
    .AddSingleton<BotService>()
    .AddSingleton<ResponderService>()
    .AddSingleton<ReplyRendererFactory>()
    .AddSingleton<IChatCompletion, AzureFoundryChat>()
    .AddSingleton<IEmbeddings, AzureFoundryEmbeddings>()
    .AddSingleton<ISpeech, AzureFoundrySpeech>()
    // Gemini provider slice: both concrete IImageGen implementations are registered as
    // themselves (not as IImageGen — that would leave DI to pick one arbitrarily), then
    // ImageGenRouter (Llm/GeminiProvider.fs) is the single IImageGen registration, reading
    // IMAGE_PROVIDER per call so `/reload-settings` can flip providers with zero restart.
    // AzureFoundryImageGen stays wired for when Azure image quota lands (see
    // AlitaBot/docs/TECH-DEBT.md) — IMAGE_PROVIDER=gemini is just today's default.
    .AddSingleton<AzureFoundryImageGen>()
    .AddSingleton<GeminiImageGen>()
    .AddSingleton<IImageGen>(fun sp ->
        ImageGenRouter(
            sp.GetRequiredService<AzureFoundryImageGen>(),
            sp.GetRequiredService<GeminiImageGen>(),
            sp.GetRequiredService<IOptions<BotConfiguration>>())
        :> IImageGen)
    .AddSingleton<IMusicGen, GeminiMusicGen>()
    // S10 PR1: natural-language tool-calling loop.
    .AddSingleton<IWebSearch, AzureResponsesWebSearch>()
    .AddSingleton<IToolExecutor, ToolExecutorService>()
    .AddSingleton<AgentToolLoop>()
    // Slice 5b: nightly per-person dossier fact extraction (Services/DossierService.fs,
    // Services/ScheduledJobs.fs). BotInfra.SchedulerHostedService needs `connString`
    // directly (the lease functions in BotInfra.ScheduledJobs are plain functions over a
    // connection string, like DbService itself) rather than through DbService, so it's
    // constructed via a factory closure here instead of relying on constructor-parameter DI.
    .AddSingleton<DossierService>()
    // Slice 8: daily morning digest (Services/DigestService.fs) — same lease-driven
    // scheduling as DossierService, both owned by SchedulerHostedService below.
    .AddSingleton<DigestService>()
    .AddSingleton<SchedulerHostedService>(fun sp ->
        new SchedulerHostedService(
            connString,
            sp.GetRequiredService<TimeProvider>(),
            AlitaScheduledJobs.jobDefinitions
                (sp.GetRequiredService<DossierService>())
                (sp.GetRequiredService<DigestService>())
                (sp.GetRequiredService<IOptions<BotConfiguration>>()),
            TimeSpan.FromMinutes 10.0,
            sp.GetRequiredService<ILogger<SchedulerHostedService>>()))
    .AddHostedService<SchedulerHostedService>(fun sp -> sp.GetRequiredService<SchedulerHostedService>())

let app = builder.Build()

%app.MapGet("/healthz", Func<string>(fun () -> "OK"))

// Test-only hook to advance the FakeTimeProvider, deterministically firing any
// pending TimeProvider-driven timers. Query: ?ms=N (default 1000).
// 404 outside TestMode; 400 if FakeTimeProvider wasn't registered.
%app.MapPost("/test/clock/advance", Func<HttpContext, Task<IResult>>(fun ctx ->
    task {
        let opts = ctx.RequestServices.GetRequiredService<IOptions<BotConfiguration>>()
        if not opts.Value.TestMode then
            return Results.NotFound()
        else
            let fake = ctx.RequestServices.GetService<FakeTimeProvider>()
            if isNull (box fake) then
                return Results.BadRequest({| error = "FakeTimeProvider not registered" |})
            else
                let ms =
                    if ctx.Request.Query.ContainsKey "ms" then
                        match Int32.TryParse(string ctx.Request.Query["ms"]) with
                        | true, v -> v
                        | _ -> 1000
                    else 1000
                fake.Advance(TimeSpan.FromMilliseconds(float ms))
                return Results.Json({| ok = true; advancedMs = ms |})
    }))

// Test-only hook (Slice 5b): starts a named scheduled job immediately, bypassing the
// lease/schedule check entirely (SchedulerHostedService.RunJobNow) — mirrors the old
// (pre-Funogram) `feature/alita-bot` branch's `/test/run-job`. 404 outside TEST_MODE, and
// also 404 for an unrecognized job name (SchedulerHostedService.IsKnownJob), rather than
// accepting the request and silently no-op-ing. RunJobNow itself is fire-and-forget (see
// its doc comment) — this handler returns 202 Accepted as soon as the job is kicked off,
// not once it's finished, because awaiting it inline held the HTTP response open long
// enough (two sequential real LLM calls against Azure AI Foundry) to exceed the CI
// real-test AKS gateway's ~15s upstream timeout (`504: upstream request timeout`).
// Callers poll the database for the job's effects instead of reading them off the
// response (DossierTests.fs / DossierRealTests.fs).
%app.MapPost("/test/run-job", Func<HttpContext, Task<IResult>>(fun ctx ->
    task {
        let opts = ctx.RequestServices.GetRequiredService<IOptions<BotConfiguration>>()
        if not opts.Value.TestMode then
            return Results.NotFound()
        else
            let jobName = string ctx.Request.Query["name"]
            let scheduler = ctx.RequestServices.GetRequiredService<SchedulerHostedService>()
            if not (scheduler.IsKnownJob jobName) then
                return Results.NotFound()
            else
                do! scheduler.RunJobNow(jobName)
                return Results.Json({| started = jobName |}, statusCode = 202)
    }))

// Reload settings endpoint
%app.MapPost("/reload-settings", Func<HttpContext, IResult>(fun ctx ->
    if not (WebhookHost.validateApiKey webhookCfg.SecretToken ctx) then
        Results.Text("Access Denied", statusCode = 401)
    else
        reloadSettings()
        ctx.RequestServices.GetRequiredService<ILogger<Root>>().LogInformation "Settings reloaded"
        Results.Ok "Settings reloaded"
))

// Main webhook endpoint with bot-specific update handling
WebhookHost.mapWebhookEndpoints webhookCfg FunogramJson.parseUpdate (fun ctx rawBody update ->
    task {
        let logger = ctx.RequestServices.GetRequiredService<ILogger<Root>>()
        use topActivity = botActivity.StartActivity("postUpdate")
        Telemetry.setUpdateIdentityTags topActivity update
        // The raw update is logged exactly once per update, before anything that can
        // fail — even a DI-resolution error below still leaves the payload in Loki.
        // RawJson makes it a real nested JSON property (not an escaped blob), and the
        // TraceId enricher ties the line to this trace; error logs don't repeat the body.
        JsonLogging.withRawJsonProperty "RawUpdate" rawBody (fun () ->
            logger.LogInformation("Received Telegram update {UpdateId}", update.UpdateId))
        try
            let bot = ctx.RequestServices.GetRequiredService<BotService>()
            do! bot.OnUpdate(update)
            %topActivity.SetTag("update-error", false)
            %topActivity.SetStatus(ActivityStatusCode.Ok)
        with ex ->
            logger.LogError(ex, "Unhandled error in update handler for {UpdateId}", update.UpdateId)
            %topActivity.SetStatus(ActivityStatusCode.Error)
            %topActivity.SetTag("update-error", true)
    }) app

app.Run()
