namespace Fizruk

open System.Threading.Tasks
open Microsoft.Extensions.Logging
open BotInfra

/// Delivers a game-status notification to every chat that controls that game.
/// Kept separate from ITelegramApi so a future frontend can plug in its own.
type INotifier =
    abstract NotifyChats: chatIds: int64 list * text: string -> Task<unit>

type TelegramNotifier(tg: ITelegramApi, logger: ILogger<TelegramNotifier>) =
    interface INotifier with
        member _.NotifyChats(chatIds, text) =
            task {
                for chatId in chatIds do
                    try
                        do! tg.CallIgnore(Funogram.Telegram.Req.SendMessage.Make(chatId, text))
                    with ex ->
                        logger.LogWarning(ex, "Fizruk: failed to notify chat {ChatId}", chatId)
            }
