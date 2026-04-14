// AlitaBot — almost human, but not quite (homage to Alita: Battle Angel)
open System
open System.Globalization
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Npgsql
open Pgvector.Npgsql
open Pgvector.Dapper
open AlitaBot
open AlitaBot.Services
open AlitaBot.Telemetry
open BotInfra
open BotInfra.DbSettings

type Root = class end

/// IOptions<BotConfiguration> that always reads the current module-level mutable.
/// Allows singletons like LlmPipeline to see settings reloaded at runtime.
type LiveBotOptions(getConf: unit -> BotConfiguration) =
    interface IOptions<BotConfiguration> with
        member _.Value = getConf()

DapperSetup.registerDateOnlyHandler()
Dapper.SqlMapper.AddTypeHandler(VectorTypeHandler())

let connString = getEnv "DATABASE_URL"

let parseStringArray (raw: string) =
    if String.IsNullOrWhiteSpace raw then [||]
    else
        try System.Text.Json.JsonSerializer.Deserialize<string[]>(raw)
        with _ ->
            // fallback: comma-separated
            raw.Split([| ','; '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun s -> s.Trim())
            |> Array.filter (fun s -> s.Length > 0)

let loadDbSettings () =
    try
        DbSettings.loadBotSettings(connString).GetAwaiter().GetResult()
    with e ->
        eprintfn "[FATAL] Failed to load bot settings from database: %O" e
        reraise()

let mutable dbSettings = loadDbSettings()

let getSetting key =
    BotSettingsAccessor(dbSettings).GetSetting key

let getSettingOr key def =
    BotSettingsAccessor(dbSettings).GetSettingOr(key, def)

let buildBotConf () =
    { BotToken    = getEnv "BOT_TELEGRAM_TOKEN"
      SecretToken = getEnv "BOT_AUTH_TOKEN"
      TargetChatId = getSettingOr "TARGET_CHAT_ID" (getEnvOr "BOT_TARGET_CHAT_ID" "0") |> int64
      BotUsername = getSettingOr "BOT_USERNAME" (getEnvOr "BOT_USERNAME" "Alita")
      TelegramApiBaseUrl =
        match getEnvOr "TELEGRAM_API_URL" "" with
        | "" -> null
        | v  -> v

      AzureFoundryEndpoint   = getEnv "AZURE_FOUNDRY_ENDPOINT"
      AzureFoundryKey        = getEnv "AZURE_FOUNDRY_KEY"
      ResponderDeployment    = getEnvOr "AZURE_FOUNDRY_RESPONDER_DEPLOYMENT"  "gpt-4o"
      RewriterDeployment     = getEnvOr "AZURE_FOUNDRY_REWRITER_DEPLOYMENT"   "gpt-4o"
      CensorDeployment       = getEnvOr "AZURE_FOUNDRY_CENSOR_DEPLOYMENT"     "gpt-4o"
      EmbeddingDeployment    = getEnvOr "AZURE_FOUNDRY_EMBEDDING_DEPLOYMENT"  "text-embedding-3-small"

      Layer3WeightSilence   = getSettingOr "LAYER3_WEIGHT_SILENCE"    "30" |> int
      Layer3WeightUsual     = getSettingOr "LAYER3_WEIGHT_USUAL"      "45" |> int
      Layer3WeightEmojiMeme = getSettingOr "LAYER3_WEIGHT_EMOJI_MEME" "20" |> int
      Layer3WeightChaos     = getSettingOr "LAYER3_WEIGHT_CHAOS"      "5"  |> int

      NewsSourceUrls         = getSettingOr "NEWS_SOURCE_URLS" "[]" |> parseStringArray
      NewsFetchIntervalHours = getSettingOr "NEWS_FETCH_INTERVAL_HOURS" "4" |> int

      ProactiveActiveHoursStart = getSettingOr "PROACTIVE_HOURS_START"       "9"   |> int
      ProactiveActiveHoursEnd   = getSettingOr "PROACTIVE_HOURS_END"         "23"  |> int
      ProactivePostProbability  = getSettingOr "PROACTIVE_POST_PROBABILITY"  "0.2" |> float

      DossierUpdateHourUtc     = getSettingOr "DOSSIER_UPDATE_HOUR_UTC"      "2"   |> int
      MessageLogRetentionDays  = getSettingOr "MESSAGE_LOG_RETENTION_DAYS"   "365" |> int
      ConversationContextMessages = getSettingOr "CONVERSATION_CONTEXT_MESSAGES" "20" |> int
      TopMemoriesPerUser          = getSettingOr "TOP_MEMORIES_PER_USER"         "5"  |> int

      TestMode = getSettingOr "TEST_MODE" "false" |> bool.Parse }

let mutable botConf = buildBotConf()

let reloadSettings () =
    dbSettings <- loadDbSettings()
    botConf    <- buildBotConf()

// NpgsqlDataSource with pgvector support (used by DbService)
let dataSource =
    let source = NpgsqlDataSourceBuilder(connString)
    %source.UseVector()
    source.Build()

let webhookCfg: WebhookConfig =
    { BotToken           = botConf.BotToken
      SecretToken        = botConf.SecretToken
      TelegramApiBaseUrl = botConf.TelegramApiBaseUrl
      OtelServiceName    = "alita-bot"
      ActivitySourceName = botActivity.Name
      MeterName          = "AlitaBot.Metrics"
      WebhookRoute       = "/bot" }

let builder = WebApplication.CreateBuilder()

WebhookHost.configureSharedServices webhookCfg builder

%builder.Services.AddSingleton(botConf)
%builder.Services.AddSingleton<IOptions<BotConfiguration>>(LiveBotOptions(fun () -> botConf))
%builder.Services.AddSingleton<NpgsqlDataSource>(dataSource)

%builder.Services.AddSingleton<DbService>(fun sp ->
    DbService(
        sp.GetRequiredService<NpgsqlDataSource>(),
        sp.GetRequiredService<TimeProvider>(),
        connString))

// Embedding service (typed HttpClient)
%builder.Services.AddHttpClient<IEmbeddingService, AzureEmbeddingService>()

// Dossier service (uses a plain HttpClient for LLM fact extraction)
%builder.Services.AddHttpClient<DossierService>()

// News service (uses a plain HttpClient for page fetching + LLM)
%builder.Services.AddHttpClient<NewsService>()

// Layer 1 responder
%builder.Services.AddHttpClient<ILlmLayer1, AzureFoundryLlm>(fun (httpClient: System.Net.Http.HttpClient) (sp: IServiceProvider) ->
    let conf = sp.GetRequiredService<BotConfiguration>()
    let logger = sp.GetRequiredService<ILogger<AzureFoundryLlm>>()
    AzureFoundryLlm(httpClient, conf, logger, conf.ResponderDeployment))

// Layer 2 rewriter
%builder.Services.AddHttpClient<ILlmLayer2, AzureFoundryLlm>(fun (httpClient: System.Net.Http.HttpClient) (sp: IServiceProvider) ->
    let conf = sp.GetRequiredService<BotConfiguration>()
    let logger = sp.GetRequiredService<ILogger<AzureFoundryLlm>>()
    AzureFoundryLlm(httpClient, conf, logger, conf.RewriterDeployment))

// Layer 3 censor
%builder.Services.AddHttpClient<ILlmLayer3, AzureFoundryLlm>(fun (httpClient: System.Net.Http.HttpClient) (sp: IServiceProvider) ->
    let conf = sp.GetRequiredService<BotConfiguration>()
    let logger = sp.GetRequiredService<ILogger<AzureFoundryLlm>>()
    AzureFoundryLlm(httpClient, conf, logger, conf.CensorDeployment))

%builder
    .Services
    .AddSingleton<LlmPipeline>()
    .AddSingleton<BotService>()
    .AddSingleton<ProactiveService>()
    .AddHostedService<ProactiveService>(fun sp -> sp.GetRequiredService<ProactiveService>())
    .AddSingleton<SchedulerService>()
    .AddHostedService<SchedulerService>(fun sp -> sp.GetRequiredService<SchedulerService>())

let app = builder.Build()

%app.MapGet("/healthz", Func<string>(fun () -> "OK"))

// Test-mode: trigger a named job immediately
%app.MapPost("/test/run-job", Func<HttpContext, Task<IResult>>(fun ctx ->
    task {
        let conf = ctx.RequestServices.GetRequiredService<BotConfiguration>()
        if not conf.TestMode then
            return Results.NotFound()
        else
            let jobName = string ctx.Request.Query["name"]
            let scheduler = ctx.RequestServices.GetRequiredService<SchedulerService>()
            do! scheduler.RunJobNow(jobName)
            return Results.Json({| ok = true; job = jobName |})
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
