namespace Fizruk

open System

[<RequireQualifiedAccess>]
type BotAction =
    | Start
    | Stop
    | Status

type ParsedCommand =
    { Action: BotAction
      GameArg: string option }

/// Pure Telegram-command parsing and game-ACL resolution — no Funogram, no Task,
/// so both are directly unit-testable and reusable by any future frontend.
module Commands =

    /// Strips a `@botname` suffix from a command word, case-insensitively. None
    /// means "addressed to a different bot"; a word with no `@` passes through as-is.
    let private stripBotSuffix (botUsername: string option) (word: string) : string option =
        match word.IndexOf '@' with
        | -1 -> Some word
        | i ->
            let cmd = word.Substring(0, i)
            let addressedTo = word.Substring(i + 1)
            match botUsername with
            | Some u when String.Equals(addressedTo, u, StringComparison.OrdinalIgnoreCase) -> Some cmd
            | _ -> None

    /// Parses raw message text into a command + optional game-name argument. None
    /// for non-commands, unrecognized commands, or a different bot's `@botname`.
    let tryParse (botUsername: string option) (text: string) : ParsedCommand option =
        if String.IsNullOrWhiteSpace text || text.[0] <> '/' then
            None
        else
            let parts = text.Split([| ' '; '\t'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
            if parts.Length = 0 then
                None
            else
                match stripBotSuffix botUsername parts.[0] with
                | None -> None
                | Some cmdWord ->
                    let action =
                        match cmdWord.ToLowerInvariant() with
                        | "/start" -> Some BotAction.Start
                        | "/stop" -> Some BotAction.Stop
                        | "/status" -> Some BotAction.Status
                        | _ -> None
                    action
                    |> Option.map (fun a ->
                        { Action = a
                          GameArg = if parts.Length > 1 then Some parts.[1] else None })

    [<RequireQualifiedAccess>]
    type Resolution =
        /// Chat id isn't in the config's `chats` map at all.
        | Unauthorized
        /// An explicit game name that isn't in this chat's allowed list.
        | Unknown of allowed: string list
        /// No name given, this chat controls more than one game, and the action needs one.
        | Ambiguous of allowed: string list
        /// No name given for /status with more than one allowed game: report all of them.
        | AllGames of allowed: string list
        | Resolved of gameId: string

    /// Resolves a command's target game against the chat's ACL. The single-game-chat
    /// shortcut (no name needed) applies to every action, including /status.
    let resolveGame (chats: Map<int64, string list>) (action: BotAction) (chatId: int64) (gameArg: string option) : Resolution =
        match chats.TryFind chatId with
        | None -> Resolution.Unauthorized
        | Some allowed ->
            match gameArg with
            | Some name ->
                // Case-insensitive match; the reply uses the configured spelling, not the user's.
                match allowed |> List.tryFind (fun g -> String.Equals(g, name, StringComparison.OrdinalIgnoreCase)) with
                | Some configuredName -> Resolution.Resolved configuredName
                | None -> Resolution.Unknown allowed
            | None ->
                match allowed with
                | [ only ] -> Resolution.Resolved only
                | _ ->
                    match action with
                    | BotAction.Status -> Resolution.AllGames allowed
                    | BotAction.Start | BotAction.Stop -> Resolution.Ambiguous allowed
