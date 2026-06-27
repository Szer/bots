// CouponHubBot — Telegram coupon management bot
open System
open System.Globalization
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Polly
open Microsoft.Extensions.Http.Resilience
open Microsoft.Extensions.Time.Testing
open CouponHubBot
open CouponHubBot.Services
open CouponHubBot.Telemetry
open BotInfra
open BotInfra.DbSettings

type Root = class end

DapperSetup.registerDateOnlyHandler()

let connString = getEnv "DATABASE_URL"

let parseAdmins (raw: string) =
    if String.IsNullOrWhiteSpace raw then
        [||]
    else
        raw.Split([| ','; ';'; ' ' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun s ->
            match Int64.TryParse(s.Trim()) with
            | true, v -> Some v
            | _ -> None)

let loadDbSettings () =
    try
        DbSettings.loadBotSettings(connString).GetAwaiter().GetResult()
    with e ->
        eprintfn "[FATAL] Failed to load bot settings from database: %O" e
        reraise()

let mutable dbSettings = loadDbSettings()

let getSetting key =
    let accessor = BotSettingsAccessor(dbSettings)
    accessor.GetSetting key

let getSettingOr key def =
    let accessor = BotSettingsAccessor(dbSettings)
    accessor.GetSettingOr(key, def)

let buildBotConf () =
    { BotToken = getEnv "BOT_TELEGRAM_TOKEN"
      SecretToken = getEnv "BOT_AUTH_TOKEN"
      CommunityChatId = getSettingOr "COMMUNITY_CHAT_ID" (getEnvOr "COMMUNITY_CHAT_ID" "-1") |> int64
      TelegramApiBaseUrl =
        match getEnvOr "TELEGRAM_API_URL" "" with
        | "" -> null
        | v -> v
      ReminderHourDublin = getSettingOr "REMINDER_HOUR_DUBLIN" "10" |> int
      ReminderRunOnStart = getSettingOr "REMINDER_RUN_ON_START" "false" |> bool.Parse
      OcrEnabled = getSettingOr "OCR_ENABLED" "false" |> bool.Parse
      OcrMaxFileSizeBytes = getSettingOr "OCR_MAX_FILE_SIZE_BYTES" (string (20L * 1024L * 1024L)) |> int64
      AzureOcrEndpoint = getSettingOr "AZURE_OCR_ENDPOINT" (getEnvOr "AZURE_OCR_ENDPOINT" "")
      AzureOcrKey = getEnvOr "AZURE_OCR_KEY" ""
      FeedbackAdminIds = getSettingOr "FEEDBACK_ADMINS" (getEnvOr "FEEDBACK_ADMINS" "") |> parseAdmins
      GitHubToken = getEnvOr "GITHUB_TOKEN" ""
      GitHubRepo = getSettingOr "GITHUB_REPO" (getEnvOr "GITHUB_REPO" "Szer/coupon-bot")
      TestMode = getSettingOr "TEST_MODE" "false" |> bool.Parse
      MaxTakenCoupons = getSettingOr "MAX_TAKEN_COUPONS" "6" |> int
      BatchDebounceMs = getSettingOr "BATCH_DEBOUNCE_MS" "5000" |> int }

let ocrConfigOf (c: BotConfiguration) =
    { OcrEnabled          = c.OcrEnabled
      OcrMaxFileSizeBytes = c.OcrMaxFileSizeBytes
      AzureOcrEndpoint    = c.AzureOcrEndpoint
      AzureOcrKey         = c.AzureOcrKey }

let botConfOptions = LiveOptions(buildBotConf())
let botOcrOptions  = LiveOptions(ocrConfigOf botConfOptions.Value)

let reloadSettings () =
    dbSettings <- loadDbSettings()
    let fresh = buildBotConf()
    botConfOptions.Set(fresh)
    botOcrOptions.Set(ocrConfigOf fresh)

let webhookCfg: WebhookConfig =
    let c = botConfOptions.Value
    { BotToken = c.BotToken
      SecretToken = c.SecretToken
      TelegramApiBaseUrl = c.TelegramApiBaseUrl
      OtelServiceName = "coupon-hub-bot"
      ActivitySourceName = botActivity.Name
      MeterName = "CouponHubBot.Metrics"
      WebhookRoute = "/bot" }

let builder = WebApplication.CreateBuilder()

WebhookHost.configureSharedServices webhookCfg builder

%builder.Services.AddSingleton<IOptions<BotConfiguration>>(botConfOptions)

// In TestMode, replace the shared TimeProvider with a FakeTimeProvider so the
// /test/clock/advance endpoint can fire BatchDebounce timers deterministically
// without making tests wait on real wall clock. Initial time is taken from
// BOT_FIXED_UTC_NOW (same env var the production FixedTimeProvider reads), so
// existing time-sensitive tests keep their fixed-clock semantics.
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

// OCR: register shared IBotOcr. Retry + per-attempt timeout live in the HTTP
// resilience pipeline rather than in the call sites — a transient failure or a
// slow attempt is retried once, and the 2s per-attempt budget keeps the batch
// flow from awaiting an in-flight call past debounce time. HttpClient.Timeout is
// disabled because it would bound the whole pipeline (all attempts share one
// budget, defeating retry); the pipeline's attempt/total timeouts govern instead.
//
// The pipeline is pinned to TimeProvider.System (real wall-clock) on purpose. A
// resilience pipeline otherwise resolves TimeProvider from DI, which in TestMode is
// a FROZEN FakeTimeProvider used for deterministic *business* time (batch debounce,
// coupon expiry). Binding HTTP *transport* timeouts to that clock means the
// per-attempt/total timeouts and the retry delay never elapse during a synchronous
// OCR call, so a stalled or failing OCR hangs until the test's real-time budget —
// the flakiness found in the #163 review. Transport timeouts must use real time
// (in production the DI clock is real, so this is a no-op there).
%builder.Services.AddSingleton<IOptions<BotOcrConfig>>(botOcrOptions)
%builder.Services
    .AddHttpClient<IBotOcr, AzureBotOcr>(fun c -> c.Timeout <- Timeout.InfiniteTimeSpan)
    .AddResilienceHandler("ocr-pipeline", fun (b: ResiliencePipelineBuilder<HttpResponseMessage>) ->
        b.TimeProvider <- TimeProvider.System
        b
            // Bound the whole operation so a webhook handler holding this call can't
            // stall: ~2s + 0.1s + 2s ≈ 4.1s worst case, well under Telegram's timeout.
            .AddTimeout(TimeSpan.FromSeconds 6.)            // total request budget (outermost)
            .AddRetry(HttpRetryStrategyOptions(
                MaxRetryAttempts = 1,
                Delay = TimeSpan.FromMilliseconds 100.,
                BackoffType = DelayBackoffType.Constant))   // retry transient failures + per-attempt timeouts
            .AddTimeout(TimeSpan.FromSeconds 2.)            // per-attempt timeout (innermost)
        |> ignore)

%builder.Services.AddHttpClient<GitHubService>()
%builder.Services.AddSingleton<CouponOcrEngine>()

%builder
    .Services
    .AddSingleton<BotService>()
    .AddSingleton<CouponFlowHandler>()
    .AddSingleton<BatchDebounce>()
    .AddSingleton<CommandHandler>()
    .AddSingleton<CallbackHandler>()
    .AddSingleton<DbService>(fun sp ->
        let opts = sp.GetRequiredService<IOptions<BotConfiguration>>()
        DbService(connString, sp.GetRequiredService<TimeProvider>(), opts.Value.MaxTakenCoupons))
    .AddSingleton<TelegramMembershipService>()
    .AddSingleton<TelegramNotificationService>()
    .AddHostedService<MembershipCacheInvalidationService>()
    .AddHostedService<BotCommandsSetupService>()
    .AddHostedService<BatchRecoveryService>()
    .AddSingleton<ReminderService>()
    .AddHostedService<ReminderService>(fun sp -> sp.GetRequiredService<ReminderService>())

let app = builder.Build()

%app.MapGet("/healthz", Func<string>(fun () -> "OK"))

// Test-only hook to advance the FakeTimeProvider, deterministically firing any
// pending TimeProvider-driven timers (notably BatchDebounce). Query: ?ms=N
// (default 1000). 404 outside TestMode; 400 if FakeTimeProvider wasn't registered.
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

// Test-only hook to trigger reminder immediately
%app.MapPost("/test/run-reminder", Func<HttpContext, Task<IResult>>(fun ctx ->
    task {
        let opts = ctx.RequestServices.GetRequiredService<IOptions<BotConfiguration>>()
        if not opts.Value.TestMode then
            return Results.NotFound()
        else
            let runner = ctx.RequestServices.GetRequiredService<ReminderService>()
            let timeProvider = ctx.RequestServices.GetRequiredService<TimeProvider>()
            let nowUtc =
                if ctx.Request.Query.ContainsKey("nowUtc") then
                    try
                        let raw = string ctx.Request.Query["nowUtc"]
                        DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal ||| DateTimeStyles.AssumeUniversal)
                    with _ ->
                        timeProvider.GetUtcNow().UtcDateTime
                else
                    timeProvider.GetUtcNow().UtcDateTime

            let! sent = runner.RunOnce(nowUtc)
            return Results.Json({| ok = true; sent = sent |})
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
WebhookHost.mapWebhookEndpoints webhookCfg (fun ctx update ->
    task {
        let logger = ctx.RequestServices.GetRequiredService<ILogger<Root>>()
        try
            let bot = ctx.RequestServices.GetRequiredService<BotService>()
            do! bot.OnUpdate(update)
        with ex ->
            logger.LogError(ex, "Unhandled error in update handler for {UpdateId}", update.Id)
    }) app

app.Run()
