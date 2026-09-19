// Fizruk — starts and stops on-demand game server Deployments from Telegram,
// and shuts them down when nobody's playing.
open System
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Fizruk
open BotInfra

type Root = class end

let config =
    try Config.loadFromFile(getEnv "FIZRUK_CONFIG_PATH")
    with ConfigError msg ->
        eprintfn "[FATAL] Fizruk config error: %s" msg
        reraise ()

let botUsername =
    match getEnvOr "BOT_USERNAME" "" with
    | "" -> None
    | u -> Some u

let webhookCfg: WebhookConfig =
    { BotToken = getEnv "BOT_TELEGRAM_TOKEN"
      SecretToken = getEnv "BOT_AUTH_TOKEN"
      TelegramApiBaseUrl =
        match getEnvOr "TELEGRAM_API_URL" "" with
        | "" -> null
        | v -> v
      OtelServiceName = "fizruk"
      ActivitySourceName = Telemetry.botActivity.Name
      MeterName = Metrics.meter.Name
      WebhookRoute = "/bot" }

let builder = WebApplication.CreateBuilder()

WebhookHost.configureSharedServices webhookCfg builder

%builder.Services.AddSingleton<FizrukConfig>(config)

// FIZRUK_FAKE_K8S selects an in-memory gateway with no cluster dependency —
// used by the container smoke tests, never set in production.
%builder.Services.AddSingleton<IK8sGateway>(fun _ ->
    if getEnvOrBool "FIZRUK_FAKE_K8S" false then
        InMemoryK8sGateway() :> IK8sGateway
    else
        let k8sConfig = k8s.KubernetesClientConfiguration.InClusterConfig()
        KubernetesGateway(new k8s.Kubernetes(k8sConfig)) :> IK8sGateway)

%builder.Services.AddSingleton<INotifier>(fun sp ->
    TelegramNotifier(sp.GetRequiredService<ITelegramApi>(), sp.GetRequiredService<ILogger<TelegramNotifier>>())
    :> INotifier)

%builder.Services.AddSingleton<GameCore>(fun sp ->
    GameCore(
        config,
        sp.GetRequiredService<IK8sGateway>(),
        sp.GetRequiredService<INotifier>(),
        sp.GetRequiredService<TimeProvider>(),
        TimeSpan.FromSeconds 20.0,
        sp.GetRequiredService<ILogger<GameCore>>()))

%builder.Services.AddSingleton<FizrukBotService>(fun sp ->
    FizrukBotService(
        config,
        sp.GetRequiredService<GameCore>(),
        sp.GetRequiredService<ITelegramApi>(),
        botUsername,
        sp.GetRequiredService<ILogger<FizrukBotService>>()))

%builder.Services.AddHostedService<IdleCheckHostedService>()

let app = builder.Build()

// No database, so readiness has nothing to check beyond the process being up.
Readiness.mapReadyEndpoint [] app

WebhookHost.mapWebhookEndpoints webhookCfg FunogramJson.parseUpdate
    (fun ctx _rawBody update ->
        task {
            let logger = ctx.RequestServices.GetRequiredService<ILogger<Root>>()
            try
                let bot = ctx.RequestServices.GetRequiredService<FizrukBotService>()
                do! bot.OnUpdate update
            with ex ->
                logger.LogError(ex, "Unhandled error in update handler for {UpdateId}", update.UpdateId)
        })
    app

app.Run()
