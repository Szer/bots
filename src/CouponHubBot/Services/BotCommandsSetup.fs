namespace CouponHubBot.Services

open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open BotInfra
open Funogram.Telegram.Types

/// At startup, sets the bot command list in Telegram so that /-autocomplete and menu show commands with descriptions.
type BotCommandsSetupService(tg: ITelegramApi, options: Microsoft.Extensions.Options.IOptions<CouponHubBot.BotConfiguration>, logger: ILogger<BotCommandsSetupService>) =
    member _.Refresh() =
        task {
            try
                let commands =
                    [| BotCommand.Create("add", "Добавить купон")
                       BotCommand.Create("list", "Доступные купоны")
                       BotCommand.Create("my", "Мои купоны")
                       BotCommand.Create("added", "Мои добавленные")
                       BotCommand.Create("stats", "Моя статистика")
                       BotCommand.Create("dashboard", "Статистика сообщества")
                       BotCommand.Create("feedback", "Фидбэк авторам бота")
                       BotCommand.Create("report", "Купон уже использован кем-то другим") |]
                let commands =
                    if options.Value.ManagedInvitesEnabled then Array.append commands [| BotCommand.Create("invite", "Ссылка для приглашения") |]
                    else commands
                let scope = BotCommandScope.AllPrivateChats(BotCommandScopeAllPrivateChats.Create("all_private_chats"))
                do! tg.CallExn(Funogram.Telegram.Req.SetMyCommands.Make(commands, scope = scope)) |> taskIgnore
                logger.LogInformation("Bot commands set for Telegram menu (/-autocomplete)")
            with ex ->
                logger.LogWarning(ex, "Could not set bot commands in Telegram; menu/autocomplete may be empty")
        }

    interface IHostedService with
        member this.StartAsync(_ct) = this.Refresh() :> Task
        member _.StopAsync(_ct: CancellationToken) = Task.CompletedTask
