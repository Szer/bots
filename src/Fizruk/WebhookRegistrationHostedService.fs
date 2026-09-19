namespace Fizruk

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open BotInfra

/// Fires BotInfra.WebhookRegistration.registerAsync at startup without blocking
/// the host from starting to serve — see that module for the opt-in behaviour.
type WebhookRegistrationHostedService
    (tg: ITelegramApi, webhookUrl: string, secretToken: string, logger: ILogger<WebhookRegistrationHostedService>) =
    interface IHostedService with
        member _.StartAsync(_ct: CancellationToken) =
            fireAndForget logger "fizruk.webhook_registration" (fun () ->
                WebhookRegistration.registerAsync tg webhookUrl secretToken 5 (TimeSpan.FromSeconds 5.0) logger)
            Task.CompletedTask

        member _.StopAsync(_ct: CancellationToken) = Task.CompletedTask
