namespace CouponHubBot.Services

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open BotInfra

/// At startup, self-registers the bot's Telegram webhook when WEBHOOK_URL is
/// configured (bot_setting key with env fallback — see Program.fs's buildBotConf).
///
/// Empty/absent WEBHOOK_URL is a strict no-op, checked BEFORE any Telegram call:
/// production has never had this setting and registers its webhook once, manually,
/// via the curl documented in README.dev.md — it must keep working untouched, so
/// absence of config here means absence of behaviour.
///
/// Exists for the real-Telegram test pipeline (tests/CouponHubBot.RealTests): the
/// transient per-run pod has no human available to run that curl step, and nothing
/// else in the repo ever called setWebhook (only deleteWebhook, in the test
/// workflow's teardown) — every real test timed out with no clear cause until this.
/// Mirrors BotCommandsSetupService's shape (one-shot startup Telegram call, log and
/// continue on failure rather than crash/boot-loop).
type WebhookRegistrationService
    (
        tg: ITelegramApi,
        options: IOptions<CouponHubBot.BotConfiguration>,
        logger: ILogger<WebhookRegistrationService>
    ) =
    interface IHostedService with
        member _.StartAsync(_ct: CancellationToken) =
            task {
                let url = options.Value.WebhookUrl
                if not (String.IsNullOrWhiteSpace url) then
                    // drop_pending_updates=true: a prior run's queued updates must not
                    // leak into a fresh run's real-Telegram assertions.
                    let req =
                        Funogram.Telegram.Req.SetWebhook.Make(
                            url,
                            dropPendingUpdates = true,
                            secretToken = options.Value.SecretToken)

                    match! tg.Call req with
                    | Ok _ -> logger.LogInformation("Webhook registered: {Url}", url)
                    | Error e ->
                        // Never log SecretToken/BotToken — only the (non-secret) URL and
                        // Telegram's own error body. A failure here is diagnosable via
                        // logs, not a boot loop — the app must keep starting either way.
                        logger.LogError(
                            "Failed to register webhook {Url}: {ErrorCode} {Description}",
                            url, e.ErrorCode, e.Description)
            }
            :> Task

        member _.StopAsync(_ct: CancellationToken) = Task.CompletedTask
