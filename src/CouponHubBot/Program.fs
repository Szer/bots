// CouponHubBot — Telegram coupon management bot
open System
open System.Data
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open System.Globalization
open Dapper
open Telegram.Bot
open Telegram.Bot.Types
open CouponHubBot
open CouponHubBot.Services
open CouponHubBot.Telemetry
open BotInfra
open BotInfra.TelegramHelpers

type Root = class end

/// Dapper type handler for DateOnly (maps to PostgreSQL DATE)
type DateOnlyTypeHandler() =
    inherit SqlMapper.TypeHandler<DateOnly>()
    override _.SetValue(parameter: IDbDataParameter, value: DateOnly) =
        parameter.Value <- value.ToDateTime(TimeOnly.MinValue)
    override _.Parse(value: obj) =
        match value with
        | :? DateOnly as d -> d
        | :? DateTime as dt -> DateOnly.FromDateTime(dt)
        | x -> failwithf "Unsupported DateOnly value: %A" x
SqlMapper.AddTypeHandler(DateOnlyTypeHandler())

let botConfJsonOptions =
    let opts = JsonSerializerOptions(JsonSerializerDefaults.Web)
    opts.NumberHandling <- JsonNumberHandling.AllowReadingFromString
    Telegram.Bot.JsonBotAPI.Configure(opts)
    opts

let globalBotConfDontUseOnlyRegister =
    let parseAdmins (raw: string) =
        if String.IsNullOrWhiteSpace raw then
            [||]
        else
            raw.Split([| ','; ';'; ' ' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.choose (fun s ->
                match Int64.TryParse(s.Trim()) with
                | true, v -> Some v
                | _ -> None)

    { BotToken = getEnv "BOT_TELEGRAM_TOKEN"
      SecretToken = getEnv "BOT_AUTH_TOKEN"
      CommunityChatId = getEnv "COMMUNITY_CHAT_ID" |> int64
      TelegramApiBaseUrl =
        match getEnvOr "TELEGRAM_API_URL" "" with
        | "" -> null
        | v -> v
      ReminderHourDublin = getEnvOr "REMINDER_HOUR_DUBLIN" "10" |> int
      ReminderRunOnStart = getEnvOrBool "REMINDER_RUN_ON_START" false
      OcrEnabled = getEnvOrBool "OCR_ENABLED" false
      OcrMaxFileSizeBytes = getEnvOrInt64 "OCR_MAX_FILE_SIZE_BYTES" (20L * 1024L * 1024L)
      AzureOcrEndpoint = getEnvOr "AZURE_OCR_ENDPOINT" ""
      AzureOcrKey = getEnvOr "AZURE_OCR_KEY" ""
      FeedbackAdminIds = getEnvOr "FEEDBACK_ADMINS" "" |> parseAdmins
      GitHubToken = getEnv "GITHUB_TOKEN"
      GitHubRepo = getEnvOr "GITHUB_REPO" "Szer/coupon-bot"
      TestMode = getEnvOrBool "TEST_MODE" false
      MaxTakenCoupons = getEnvOr "MAX_TAKEN_COUPONS" "6" |> int }

let validateApiKey (ctx: HttpContext) =
    let botConf = ctx.RequestServices.GetRequiredService<BotConfiguration>()
    match ctx.Request.Headers.TryGetValue "X-Telegram-Bot-Api-Secret-Token" with
    | true, headerValues when headerValues.Count > 0 && headerValues[0] = botConf.SecretToken -> true
    | _ -> false

let builder = WebApplication.CreateBuilder()

// Configure Serilog for structured JSON logging with trace correlation
Observability.configureSerilog builder.Host

%builder.Services.AddSingleton globalBotConfDontUseOnlyRegister
%builder.Services.AddSingleton<TimeProvider>(fun _sp -> Time.fromEnvironment ())
// Configure JSON options for Telegram.Bot compatibility
%builder.Services.Configure<JsonSerializerOptions>(fun (opts: JsonSerializerOptions) ->
    opts.NumberHandling <- JsonNumberHandling.AllowReadingFromString
    JsonBotAPI.Configure(opts)
)

%builder.Services
    .AddHttpClient("telegram_bot_client")
    .AddTypedClient(fun httpClient _sp ->
        let botConf = _sp.GetRequiredService<BotConfiguration>()
        let options =
            if isNull botConf.TelegramApiBaseUrl then
                TelegramBotClientOptions(botConf.BotToken)
            else
                // Telegram.Bot will omit path/query/fragment; we only need scheme://host:port
                TelegramBotClientOptions(botConf.BotToken, botConf.TelegramApiBaseUrl)
        TelegramBotClient(options, httpClient) :> ITelegramBotClient)

%builder.Services.AddHttpClient<IAzureTextOcr, AzureOcrService>()
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
        DbService(getEnv "DATABASE_URL", sp.GetRequiredService<TimeProvider>(), botConf.MaxTakenCoupons))
    .AddSingleton<TelegramMembershipService>()
    .AddSingleton<TelegramNotificationService>()
    .AddHostedService<MembershipCacheInvalidationService>()
    .AddHostedService<BotCommandsSetupService>()
    .AddSingleton<ReminderService>()
    .AddHostedService<ReminderService>(fun sp -> sp.GetRequiredService<ReminderService>())

%Observability.addBotOpenTelemetry "coupon-hub-bot" botActivity.Name "CouponHubBot.Metrics" builder.Services

let app = builder.Build()

// Health check
%app.MapGet("/health", Func<string>(fun () -> "OK"))
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

// Main webhook endpoint
%app.MapPost("/bot", Func<HttpContext, Task<IResult>>(fun ctx ->
    task {
        // Validate API key
        if not (validateApiKey ctx) then
            ctx.Response.StatusCode <- 401
            return Results.Text("Access Denied")
        else
            let logger = ctx.RequestServices.GetRequiredService<ILogger<Root>>()

            // Deserialize Update from request body
            let! update = JsonSerializer.DeserializeAsync<Update>(ctx.Request.Body, telegramJsonOptions)

            if isNull update then
                return Results.BadRequest()
            else

            try
                let bot = ctx.RequestServices.GetRequiredService<BotService>()
                do! bot.OnUpdate(update)
                return Results.Ok()
            with ex ->
                logger.LogError(ex, "Unhandled error in update handler for {UpdateId}", update.Id)
                return Results.Ok()
    }))

app.Run()
