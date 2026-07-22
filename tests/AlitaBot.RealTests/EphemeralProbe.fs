/// Ephemeral-message re-probe (`dotnet run -- probe-ephemeral`), now that the test
/// bots are GROUP ADMINS in both test chats (see src/AlitaBot/README.md's "Ephemeral
/// message probe" — the original Slice 4 probe hit `400 BOT_NOT_ADMIN` as a plain
/// member). Same shape as DraftProbe.fs: calls the Bot API directly over HTTP
/// (bypassing Funogram/BotHelpers) so raw ok/result/error_code/description are visible
/// verbatim, while a logged-in MTProto user client watches every raw update it
/// receives and polls `Messages_GetHistory` before/after.
module AlitaBot.RealTests.EphemeralProbe

open System
open System.Net.Http
open System.Net.Http.Json
open System.Text.Json
open System.Threading.Tasks
open TL

/// One HTTP call to the Bot API, printed verbatim (ok / result / error_code / description).
let private call (http: HttpClient) (token: string) (method_: string) (payload: obj) =
    task {
        let! resp = http.PostAsJsonAsync($"https://api.telegram.org/bot{token}/{method_}", payload)
        let! body = resp.Content.ReadAsStringAsync()
        printfn "  HTTP %s %s" method_ (JsonSerializer.Serialize payload)
        printfn "  -> %s" body
        return JsonDocument.Parse body
    }

let private describeUpdate (u: Update) : string option =
    match u with
    | :? UpdateNewMessage as nm ->
        match nm.message with
        | :? TL.Message as m ->
            Some $"UpdateNewMessage id={m.id} peer={m.peer_id} replyTo=%A{m.ReplyTo} text=%A{m.message}"
        | other -> Some $"UpdateNewMessage (non-text) {other.GetType().Name}"
    | :? UpdateEditMessage as em ->
        match em.message with
        | :? TL.Message as m -> Some $"UpdateEditMessage id={m.id} peer={m.peer_id} text=%A{m.message}"
        | other -> Some $"UpdateEditMessage (non-text) {other.GetType().Name}"
    | :? UpdateUserTyping as ut -> Some $"UpdateUserTyping user={ut.user_id} action={ut.action.GetType().Name}"
    | :? UpdateChatUserTyping as ct -> Some $"UpdateChatUserTyping chat={ct.chat_id} action={ct.action.GetType().Name}"
    | other -> Some $"(other) {other.GetType().Name}"

let private logUpdates (updates: UpdatesBase) =
    for u in updates.UpdateList do
        match describeUpdate u with
        | Some line -> printfn "  [user-client saw] %s  (t=%s)" line (DateTime.UtcNow.ToString "HH:mm:ss.fff")
        | None -> ()

let runAsync () : Task<int> =
    task {
        let env = RealEnv.load ()

        if not env.CanLogin then
            eprintfn "probe-ephemeral: ALITA_TG_API_ID/HASH/PHONE missing in %s" RealEnv.envFilePath
            return 1
        elif String.IsNullOrWhiteSpace env.BotToken then
            eprintfn "probe-ephemeral: ALITA_TEST_BOT_TOKEN missing in %s" RealEnv.envFilePath
            return 1
        elif env.TestChatId = 0L then
            eprintfn "probe-ephemeral: ALITA_TEST_CHAT_ID missing in %s" RealEnv.envFilePath
            return 1
        else
            use client = new TgUserClient(env.TgApiId, env.TgApiHash, env.TgSessionPath, env.TgPhone)
            client.AddRawUpdateSink logUpdates
            let! me = client.LoginAsync()
            printfn "logged in as %s (id=%d) — this account is BOTH the sender and the ephemeral receiver" me.first_name me.id

            use http = new HttpClient(Timeout = TimeSpan.FromSeconds 15.)

            printfn "\n=== chat %d, bot token prefix %d ===" env.TestChatId env.BotUserId

            let! before = client.RecentMessageIds(env.TestChatId, 8)
            printfn "history before: [%s]" (String.Join(",", before))

            let marker = Guid.NewGuid().ToString "N"
            let! triggerMsgId = client.SendText(env.TestChatId, $"probe-ephemeral trigger {marker}")
            printfn "sent trigger message, MTProto id=%d" triggerMsgId
            do! Task.Delay 1500

            // The actual probe call: sendMessage with receiver_user_id = this account's
            // own id (the only receiver this single-account harness CAN observe from).
            printfn "\n--- sendMessage WITH receiver_user_id (ephemeral) ---"
            let! doc =
                call
                    http
                    env.BotToken
                    "sendMessage"
                    {| chat_id = env.TestChatId
                       text = $"ephemeral reply to {marker}"
                       receiver_user_id = me.id |}

            let ok = doc.RootElement.GetProperty("ok").GetBoolean()

            if ok then
                let result = doc.RootElement.GetProperty "result"

                let messageId =
                    match result.TryGetProperty "message_id" with
                    | true, v -> string (v.GetInt64())
                    | _ -> "<missing>"

                let ephemeralId =
                    match result.TryGetProperty "ephemeral_message_id" with
                    | true, v -> string (v.GetInt64())
                    | _ -> "<missing>"

                printfn "ACCEPTED — message_id=%s ephemeral_message_id=%s" messageId ephemeralId
            else
                let code =
                    match doc.RootElement.TryGetProperty "error_code" with
                    | true, v -> string (v.GetInt32())
                    | _ -> "?"

                let desc =
                    match doc.RootElement.TryGetProperty "description" with
                    | true, v -> v.GetString()
                    | _ -> "?"

                printfn "REJECTED %s: %s" code desc

            printfn "\nwatching for updates for 20s (raw update kinds print above as they arrive)..."
            do! Task.Delay 20000

            let! after = client.RecentMessageIds(env.TestChatId, 8)
            printfn "\nhistory after: [%s]" (String.Join(",", after))
            printfn "new ids in history: [%s]" (String.Join(",", Set.difference (Set.ofArray after) (Set.ofArray before)))

            // Control: a plain (non-ephemeral) send in the same chat, to confirm the
            // update pump / peer resolution work normally post-migration.
            printfn "\n--- control: sendMessage WITHOUT receiver_user_id ---"
            let! _ =
                call http env.BotToken "sendMessage" {| chat_id = env.TestChatId; text = $"control reply to {marker}" |}

            printfn "watching for updates for 10s..."
            do! Task.Delay 10000
            let! afterControl = client.RecentMessageIds(env.TestChatId, 8)
            printfn "history after control: [%s]" (String.Join(",", afterControl))

            printfn "\nprobe-ephemeral DONE — see '[user-client saw]' lines for what the receiver's own MTProto client observed."
            return 0
    }
