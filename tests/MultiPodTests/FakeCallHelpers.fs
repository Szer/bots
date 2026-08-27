namespace MultiPodTests

open System.Text.Json
open BotTestInfra

/// Same minimal FakeTgApi call-body parsing as CouponHubBot.Tests/FakeCallHelpers.fs — not
/// shared via a project reference since a test project referencing another test project is
/// unusual here; this is the small subset this suite's smoke tests need.
module FakeCallHelpers =
    let private tryGetString (root: JsonElement) (prop: string) =
        match root.TryGetProperty(prop) with
        | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
        | _ -> None

    let private tryGetInt64 (root: JsonElement) (prop: string) =
        match root.TryGetProperty(prop) with
        | true, v when v.ValueKind = JsonValueKind.Number -> Some(v.GetInt64())
        | _ -> None

    /// True if any call's body has the given chat_id and its text/caption contains `substring`.
    let findCallWithText (calls: FakeCall array) (chatId: int64) (substring: string) : bool =
        calls
        |> Array.exists (fun call ->
            try
                use doc = JsonDocument.Parse(call.Body)
                let root = doc.RootElement
                tryGetInt64 root "chat_id" = Some chatId
                && (tryGetString root "text" |> Option.exists (fun t -> t.Contains substring)
                    || tryGetString root "caption" |> Option.exists (fun c -> c.Contains substring))
            with _ -> false)

    /// Count of matching calls -- used to dedupe cross-pod sends by content, since FakeTgApi's
    /// call log carries no instance identity.
    let countCallsWithText (calls: FakeCall array) (chatId: int64) (substring: string) : int =
        calls
        |> Array.filter (fun call ->
            try
                use doc = JsonDocument.Parse(call.Body)
                let root = doc.RootElement
                tryGetInt64 root "chat_id" = Some chatId
                && (tryGetString root "text" |> Option.exists (fun t -> t.Contains substring)
                    || tryGetString root "caption" |> Option.exists (fun c -> c.Contains substring))
            with _ -> false)
        |> Array.length

    /// Calls that look like the album bulk-confirm message -- same convention as
    /// CouponHubBot.Tests/BatchTestHelpers.fs's bulkConfirmCalls.
    let bulkConfirmCallCount (calls: FakeCall array) (chatId: int64) : int =
        calls
        |> Array.filter (fun call ->
            try
                use doc = JsonDocument.Parse(call.Body)
                let root = doc.RootElement
                tryGetInt64 root "chat_id" = Some chatId
                && (tryGetString root "text"
                    |> Option.exists (fun t -> t.Contains "Подтвердить" || t.Contains "Не смог распознать ни одного"))
            with _ -> false)
        |> Array.length
