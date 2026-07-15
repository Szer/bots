namespace CouponHubBot.Services

open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open BotInfra
open Funogram.Telegram.Types

/// At startup, sets the bot command list in Telegram so that /-autocomplete and menu show commands with descriptions.
type BotCommandsSetupService(tg: ITelegramApi, logger: ILogger<BotCommandsSetupService>) =
    interface IHostedService with
        member _.StartAsync(_ct: CancellationToken) =
            task {
                try
                    let commands =
                        [| BotCommand.Create("add", "Добавить купон")
                           BotCommand.Create("list", "Доступные купоны")
                           BotCommand.Create("my", "Мои купоны")
                           BotCommand.Create("added", "Мои добавленные")
                           BotCommand.Create("stats", "Моя статистика")
                           BotCommand.Create("feedback", "Фидбэк авторам бота") |]
                    let scope = BotCommandScope.AllPrivateChats(BotCommandScopeAllPrivateChats.Create("all_private_chats"))
                    do! tg.CallExn(Funogram.Telegram.Req.SetMyCommands.Make(commands, scope = scope)) |> taskIgnore
                    logger.LogInformation("Bot commands set for Telegram menu (/-autocomplete)")
                with ex ->
                    logger.LogWarning(ex, "Could not set bot commands in Telegram; menu/autocomplete may be empty")
            }
            :> Task

        member _.StopAsync(_ct: CancellationToken) = Task.CompletedTask
