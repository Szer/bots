namespace AlitaBot.Services

open System
open System.Threading.Tasks
open Funogram.Telegram.Types
open AlitaBot

/// A registered command's full handler signature: conf, the triggering message, its
/// sender, and the text after the command word (trimmed; "" when no args).
type CommandHandler = BotConfiguration -> Message -> User -> string -> Task<unit>

/// One row of the command registry (Phase-1 Slice 4) — Name/Aliases/Description double
/// as both the dispatcher's match table and /help's auto-generated listing
/// (registry-as-source-of-truth: there is no separately hand-written help text to drift
/// out of sync with the actual command set).
type CommandDef =
    { Name: string
      Aliases: string list
      Description: string
      Handler: CommandHandler }

/// Grows S3's `BotService.tryParseCommand` (which only knew about `/img`) into a small
/// registry shared by every command.
module Commands =

    /// Recognizes `/cmd`, `!cmd`, and `/cmd@{BotUsername}` at the start of `text`
    /// (case-sensitive, matching Telegram convention) and looks the command name up
    /// against `defs` by Name or Aliases. `/cmd@{someOtherBot}` never matches — it's
    /// addressed to a different bot in the same group, so it falls through to the
    /// normal message/trigger path exactly like an unrecognized command (`!cmd@bot`
    /// isn't a form Telegram produces, so it's rejected too). Returns the matched
    /// definition plus the trimmed remainder of the text (the command's "args").
    let tryMatch (conf: BotConfiguration) (defs: CommandDef list) (text: string) : (CommandDef * string) option =
        if String.IsNullOrEmpty text || (text[0] <> '/' && text[0] <> '!') then
            None
        else
            let spaceIdx = text.IndexOfAny([| ' '; '\n'; '\t' |])
            let firstWord = if spaceIdx < 0 then text else text.Substring(0, spaceIdx)
            let args = if spaceIdx < 0 then "" else text.Substring(spaceIdx + 1).Trim()
            let isBang = firstWord[0] = '!'
            let body = firstWord.Substring(1)

            let namePart, botPart =
                match body.IndexOf '@' with
                | -1 -> body, None
                | i -> body.Substring(0, i), Some(body.Substring(i + 1))

            match botPart with
            | Some _ when isBang -> None
            | Some b when b <> conf.BotUsername -> None
            | _ when namePart = "" -> None
            | _ ->
                defs
                |> List.tryFind (fun d -> d.Name = namePart || List.contains namePart d.Aliases)
                |> Option.map (fun d -> d, args)

    /// Auto-generated RU /help text — one line per registered command (see CommandDef).
    let helpText (defs: CommandDef list) : string =
        let line (d: CommandDef) =
            let names = d.Name :: d.Aliases |> List.map (fun n -> $"/{n}") |> String.concat " | "
            $"{names} — {d.Description}"

        "Доступные команды:\n" + (defs |> List.map line |> String.concat "\n")
