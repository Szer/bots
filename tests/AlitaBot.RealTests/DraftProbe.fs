/// M5 empirical probe (`dotnet run -- probe-draft`): finds out, against real
/// Telegram, what Bot API 10.x `sendMessageDraft`/`sendRichMessageDraft` actually
/// do — before DraftRenderer is implemented for real. Calls the Bot API directly
/// over HTTP (bypassing Funogram/BotHelpers) so raw ok/error_code/description are
/// visible verbatim, and drives a logged-in MTProto user client in parallel to see
/// what the human side of the chat actually receives.
///
/// No tunnel/webhook/bot process needed — this only exercises the Bot API and the
/// user client, both directly reachable from here.
module AlitaBot.RealTests.DraftProbe

open System
open System.Net.Http
open System.Net.Http.Json
open System.Text.Json
open System.Threading.Tasks
open TL

/// One HTTP call to the Bot API, printed verbatim (ok / error_code / description).
let private call (http: HttpClient) (token: string) (method_: string) (payload: obj) =
    task {
        let! resp = http.PostAsJsonAsync($"https://api.telegram.org/bot{token}/{method_}", payload)
        let! body = resp.Content.ReadAsStringAsync()
        let doc = JsonDocument.Parse body
        let ok = doc.RootElement.GetProperty("ok").GetBoolean()

        let detail =
            if ok then
                "ok"
            else
                let code =
                    match doc.RootElement.TryGetProperty "error_code" with
                    | true, v -> string (v.GetInt32())
                    | _ -> "?"

                let desc =
                    match doc.RootElement.TryGetProperty "description" with
                    | true, v -> v.GetString()
                    | _ -> "?"

                $"FAILED {code}: {desc}"

        printfn "  HTTP %s %s -> %s" method_ (JsonSerializer.Serialize payload) detail
        return ok
    }

/// Describes a raw MTProto update relevant to the probe: draft/typing actions,
/// new messages, and edits. Everything else is skipped (read receipts, etc. would
/// otherwise drown the log).
let private describeUpdate (u: Update) : string option =
    let describeAction (action: SendMessageAction) =
        match action with
        | :? SendMessageTextDraftAction as a -> $"TextDraft randomId={a.random_id} text=%A{a.text.text}"
        | :? SendMessageRichMessageDraftAction as a -> $"RichMessageDraft randomId={a.random_id}"
        | :? SendMessageCancelAction -> "Cancel"
        | :? SendMessageTypingAction -> "Typing"
        | other -> other.GetType().Name

    match u with
    | :? UpdateUserTyping as ut -> Some $"UpdateUserTyping user={ut.user_id} action=[{describeAction ut.action}]"
    | :? UpdateChatUserTyping as ct ->
        let from =
            match ct.from_id with
            | :? PeerUser as p -> string p.user_id
            | other -> other.GetType().Name

        Some $"UpdateChatUserTyping chat={ct.chat_id} from={from} action=[{describeAction ct.action}]"
    | :? UpdateNewMessage as nm ->
        match nm.message with
        | :? TL.Message as m -> Some $"UpdateNewMessage id={m.id} peer={m.peer_id} text=%A{m.message}"
        | other -> Some $"UpdateNewMessage (non-text) {other.GetType().Name}"
    | :? UpdateEditMessage as em ->
        match em.message with
        | :? TL.Message as m -> Some $"UpdateEditMessage id={m.id} peer={m.peer_id} text=%A{m.message}"
        | other -> Some $"UpdateEditMessage (non-text) {other.GetType().Name}"
    | _ -> None

let private logUpdates (updates: UpdatesBase) =
    for u in updates.UpdateList do
        match describeUpdate u with
        | Some line -> printfn "  [user-client saw] %s  (t=%s)" line (DateTime.UtcNow.ToString "HH:mm:ss.fff")
        | None -> ()

/// draftId is Telegram's `random_id` for the draft-action RPC — any int64 works,
/// re-used across calls to the same logical draft so Telegram treats them as
/// updates to one in-flight draft rather than unrelated ones.
let private randomDraftId () =
    let bytes = Guid.NewGuid().ToByteArray()
    BitConverter.ToInt64(bytes, 0) &&& 0x7FFFFFFFFFFFFFFFL

/// Runs the sendMessageDraft -> sendMessageDraft -> sendMessage sequence against
/// one chat and reports what happened on both sides (Bot API + user client).
///
/// `botApiChatId` is what the BOT calls its chat_id. `historyPeerChatId` is the
/// chat_id the USER CLIENT must resolve to see the same conversation. For a group
/// these are identical (both sides sit in the same chat entity). For a private
/// chat they are NOT: Telegram's private-chat ids are symmetric-but-swapped — the
/// bot's chat_id for "my DM with the human" equals the human's user id, while the
/// human's peer for "my DM with the bot" equals the bot's user id.
let private probeChat
    (http: HttpClient)
    (client: TgUserClient)
    (token: string)
    (label: string)
    (botApiChatId: int64)
    (historyPeerChatId: int64)
    =
    task {
        printfn "\n=== probing %s (bot chat_id=%d, user-side peer=%d) ===" label botApiChatId historyPeerChatId

        let! before = client.RecentMessageIds(historyPeerChatId, 5)
        printfn "  history before: [%s]" (String.Join(",", before))

        let draftId = randomDraftId ()
        printfn "  draftId=%d" draftId

        let! ok1 =
            call
                http
                token
                "sendMessageDraft"
                {| chat_id = botApiChatId
                   draft_id = draftId
                   text = "думаю..." |}

        do! Task.Delay 2000

        let! ok2 =
            call
                http
                token
                "sendMessageDraft"
                {| chat_id = botApiChatId
                   draft_id = draftId
                   text = "думаю... сейчас отвечу, дай секунду собраться с мыслями" |}

        do! Task.Delay 2000

        let finalText = $"готово — вот финальный ответ ({label})"

        let! ok3 =
            call
                http
                token
                "sendMessage"
                {| chat_id = botApiChatId
                   text = finalText |}

        do! Task.Delay 1500 // let the final message's update propagate to the user client
        let! after = client.RecentMessageIds(historyPeerChatId, 5)
        printfn "  history after: [%s]" (String.Join(",", after))

        return
            {| Label = label
               ChatId = botApiChatId
               DraftCall1Ok = ok1
               DraftCall2Ok = ok2
               FinalSendOk = ok3
               HistoryBefore = before
               HistoryAfter = after |}
    }

let runAsync () : Task<int> =
    task {
        let env = RealEnv.load ()

        if not env.CanLogin then
            eprintfn "probe-draft: ALITA_TG_API_ID/HASH/PHONE missing in %s" RealEnv.envFilePath
            return 1
        elif String.IsNullOrWhiteSpace env.BotToken || String.IsNullOrWhiteSpace env.BotUsername then
            eprintfn "probe-draft: ALITA_TEST_BOT_TOKEN/ALITA_TEST_BOT_USERNAME missing in %s" RealEnv.envFilePath
            return 1
        elif env.TestChatId = 0L then
            eprintfn "probe-draft: ALITA_TEST_CHAT_ID missing in %s" RealEnv.envFilePath
            return 1
        else
            use client = new TgUserClient(env.TgApiId, env.TgApiHash, env.TgSessionPath, env.TgPhone)
            client.AddRawUpdateSink logUpdates
            let! user = client.LoginAsync()
            printfn "logged in as %s (id=%d)" user.first_name user.id

            use http = new HttpClient(Timeout = TimeSpan.FromSeconds 15.)

            printfn "\n=== resolving DM with @%s ===" env.BotUsername
            let! botUserId = client.ResolveUserByUsername env.BotUsername
            printfn "  bot user id = %d (token prefix = %d)" botUserId env.BotUserId

            if botUserId <> env.BotUserId then
                printfn
                    "  WARNING: resolved id %d != token-derived id %d — is ALITA_TEST_BOT_USERNAME actually this bot?"
                    botUserId
                    env.BotUserId

            // The bot can only message a user's DM after that user has messaged it
            // at least once — send /start so the DM chat exists from the bot's side.
            let! _ = client.SendText(botUserId, "/start")
            printfn "  sent /start to the bot's DM"
            do! Task.Delay 1000

            let! groupResult = probeChat http client env.BotToken "GROUP" env.TestChatId env.TestChatId
            do! Task.Delay 3000 // avoid overlapping draft/typing state between the two probes
            // DM: the bot's chat_id for "my DM with the human" is the human's own id
            // (user.id, this account) — NOT the bot's id (that would be the bot
            // messaging itself, which Telegram rejects with 403).
            let! dmResult = probeChat http client env.BotToken "DM" user.id botUserId

            printfn "\n=== SUMMARY ==="

            for r in [ groupResult; dmResult ] do
                printfn
                    "%s (chat_id=%d): draft1=%b draft2=%b finalSend=%b historyBefore=[%s] historyAfter=[%s]"
                    r.Label
                    r.ChatId
                    r.DraftCall1Ok
                    r.DraftCall2Ok
                    r.FinalSendOk
                    (String.Join(",", r.HistoryBefore))
                    (String.Join(",", r.HistoryAfter))

            printfn "\nprobe-draft DONE — see the '[user-client saw]' lines above for what the human side observed."
            return 0
    }
