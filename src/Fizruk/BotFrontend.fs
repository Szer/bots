namespace Fizruk

open System
open System.Threading.Tasks
open Funogram.Telegram.Types
open Microsoft.Extensions.Logging
open BotInfra

/// Thin Telegram frontend: parse -> resolve -> call GameCore -> reply. Game
/// semantics live in GameCore/Commands so a second frontend can reuse them.
type FizrukBotService(config: FizrukConfig, core: GameCore, tg: ITelegramApi, botUsername: string option, logger: ILogger<FizrukBotService>) =

    let sendText (chatId: int64) (text: string) =
        tg.CallIgnore(Funogram.Telegram.Req.SendMessage.Make(chatId, text))

    let allowedGamesText (allowed: string list) = "This chat controls: " + String.Join(", ", allowed)

    member _.Handle(chatId: int64, action: BotAction, gameArg: string option) : Task<unit> =
        task {
            Metrics.commandTotal.Add(
                1L,
                Collections.Generic.KeyValuePair("action", box (string action)),
                Collections.Generic.KeyValuePair("game", box (gameArg |> Option.defaultValue "")))

            match Commands.resolveGame config.Chats action chatId gameArg with
            | Commands.Resolution.Unauthorized -> ()
            | Commands.Resolution.Unknown allowed -> do! sendText chatId (allowedGamesText allowed)
            | Commands.Resolution.Ambiguous allowed -> do! sendText chatId ("Which game? " + allowedGamesText allowed)
            | Commands.Resolution.AllGames allowed ->
                let! texts = allowed |> List.map core.Status |> Task.WhenAll
                do! sendText chatId (String.Join("\n\n", texts))
            | Commands.Resolution.Resolved gameId ->
                let! reply =
                    match action with
                    | BotAction.Start -> core.Start gameId
                    | BotAction.Stop -> core.Stop gameId
                    | BotAction.Status -> core.Status gameId
                do! sendText chatId reply
        }

    /// Only messages from a chat id present in `chats` are handled; everything else
    /// (unknown chats, non-commands, malformed updates) is ignored silently.
    member this.OnUpdate(update: Update) : Task<unit> =
        task {
            match update.Message |> Option.bind (fun m -> m.Text |> Option.map (fun t -> m, t)) with
            | None -> ()
            | Some(msg, text) ->
                let chatId = msg.Chat.Id
                if config.Chats.ContainsKey chatId then
                    match Commands.tryParse botUsername text with
                    | None -> ()
                    | Some cmd ->
                        try
                            do! this.Handle(chatId, cmd.Action, cmd.GameArg)
                        with ex ->
                            logger.LogError(ex, "Fizruk: error handling command in chat {ChatId}", chatId)
        }
