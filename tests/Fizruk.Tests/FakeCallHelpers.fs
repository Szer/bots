module Fizruk.Tests.FakeCallHelpers

open System.Text.Json
open BotTestInfra

/// The chat_id and text fields of a FakeTgApi-recorded sendMessage call body.
type ParsedCall =
    { ChatId: int64 option
      Text: string option }

let parseCallBody (body: string) : ParsedCall option =
    try
        use doc = JsonDocument.Parse body
        let root = doc.RootElement
        let chatId =
            match root.TryGetProperty "chat_id" with
            | true, v when v.ValueKind = JsonValueKind.Number -> Some(v.GetInt64())
            | _ -> None
        let text =
            match root.TryGetProperty "text" with
            | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
            | _ -> None
        Some { ChatId = chatId; Text = text }
    with _ ->
        None

/// True if any call has the given chat id and its text contains the substring.
let anyCallHasText (calls: FakeCall array) (chatId: int64) (textSubstring: string) : bool =
    calls
    |> Array.exists (fun call ->
        match parseCallBody call.Body with
        | Some { ChatId = Some cid; Text = Some text } -> cid = chatId && text.Contains textSubstring
        | _ -> false)
