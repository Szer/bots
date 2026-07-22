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
      ModelAllowlistJson = getSettingOr "MODEL_ALLOWLIST" "[]"
      SummaryPrompt = getSettingOr "SUMMARY_PROMPT" ""
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
    .AddSingleton<IImageGen, AzureFoundryImageGen>()

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
