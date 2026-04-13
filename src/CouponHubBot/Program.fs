// CouponHubBot — Telegram coupon management bot
open System
open System.Globalization
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
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
      MaxTakenCoupons = getSettingOr "MAX_TAKEN_COUPONS" "6" |> int }

let mutable botConf = buildBotConf()

let reloadSettings () =
    dbSettings <- loadDbSettings()
    botConf <- buildBotConf()

let webhookCfg: WebhookConfig =
    { BotToken = botConf.BotToken
      SecretToken = botConf.SecretToken
      TelegramApiBaseUrl = botConf.TelegramApiBaseUrl
      OtelServiceName = "coupon-hub-bot"
      ActivitySourceName = botActivity.Name
      MeterName = "CouponHubBot.Metrics"
      WebhookRoute = "/bot" }

let builder = WebApplication.CreateBuilder()

WebhookHost.configureSharedServices webhookCfg builder

%builder.Services.AddSingleton(botConf)

// OCR: register shared IBotOcr
%builder.Services.AddSingleton<BotOcrConfig>(
    { OcrEnabled = botConf.OcrEnabled
      AzureOcrEndpoint = botConf.AzureOcrEndpoint
      AzureOcrKey = botConf.AzureOcrKey })
%builder.Services.AddHttpClient<IBotOcr, AzureBotOcr>()

%builder.Services.AddHttpClient<GitHubService>()
%builder.Services.AddSingleton<CouponOcrEngine>()

%builder
    .Services
    .AddSingleton<BotService>()
    .AddSingleton<CouponFlowHandler>()
    .AddSingleton<CommandHandler>()
    .AddSingleton<CallbackHandler>()
    .AddSingleton<DbService>(fun sp ->
        let botConf = sp.GetRequiredService<BotConfiguration>()
        DbService(connString, sp.GetRequiredService<TimeProvider>(), botConf.MaxTakenCoupons))
    .AddSingleton<TelegramMembershipService>()
    .AddSingleton<TelegramNotificationService>()
    .AddHostedService<MembershipCacheInvalidationService>()
    .AddHostedService<BotCommandsSetupService>()
    .AddSingleton<ReminderService>()
    .AddHostedService<ReminderService>(fun sp -> sp.GetRequiredService<ReminderService>())

let app = builder.Build()

%app.MapGet("/healthz", Func<string>(fun () -> "OK"))

// Test-only hook to trigger reminder immediately
%app.MapPost("/test/run-reminder", Func<HttpContext, Task<IResult>>(fun ctx ->
    task {
        let botConf = ctx.RequestServices.GetRequiredService<BotConfiguration>()
        if not botConf.TestMode then
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
