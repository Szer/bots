namespace AlitaBot.RealTests

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open TL

/// MTProto user client (WTelegramClient) playing the human in the test group.
/// Incoming updates are recorded and polled every 500ms by the Await* helpers.
/// Bot API supergroup ids (-100xxxxxxxxxx) are translated to MTProto channel
/// ids; access hashes are resolved via Messages_GetAllDialogs and cached.
type TgUserClient(apiId: string, apiHash: string, sessionPath: string, phone: string, ?prompt: string -> string) =

    static let pollInterval = TimeSpan.FromMilliseconds 500.
    static let editsSettleTimeout = TimeSpan.FromSeconds 90.

    /// Non-interactive by default: tests must never block on stdin.
    let prompt =
        defaultArg prompt (fun key ->
            failwith $"WTelegram asked for '{key}' — interactive login required, run `make tg-login` first")

    let config (key: string) : string =
        match key with
        | "api_id" -> apiId
        | "api_hash" -> apiHash
        | "phone_number" -> phone
        | "session_pathname" -> sessionPath
        | "verification_code"
        | "password" -> prompt key
        | _ -> Unchecked.defaultof<string> // fall back to WTelegram defaults

    let client = new WTelegram.Client(Func<string, string> config)

    let gate = obj ()
    let newMessages = ResizeArray<TL.Message>()

    /// Probe-only hook: fires for every raw update, unfiltered (drafts surface as
    /// UpdateUserTyping/UpdateChatUserTyping with a SendMessage*DraftAction, not as
    /// UpdateNewMessage/UpdateEditMessage — the default `record` path never sees them).
    let rawSinks = ResizeArray<UpdatesBase -> unit>()

    /// (peer id, message id) -> (last new/edit seen at UTC, latest text).
    let lastActivity = ConcurrentDictionary<struct (int64 * int), struct (DateTime * string)>()

    /// Bot API chat id -> resolved InputPeer (with access hash).
    let peers = ConcurrentDictionary<int64, InputPeer>()

    let mutable me: TL.User = Unchecked.defaultof<TL.User>

    /// MTProto peer id for a Bot API chat id: -100xxxxxxxxxx -> xxxxxxxxxx (channel),
    /// -yyy -> yyy (basic group), positive ids unchanged (user).
    let peerKey (botApiChatId: int64) =
        if botApiChatId <= -1_000_000_000_000L then -botApiChatId - 1_000_000_000_000L
        elif botApiChatId < 0L then -botApiChatId
        else botApiChatId

    let peerMatches (botApiChatId: int64) (peer: Peer) =
        let key = peerKey botApiChatId

        match peer with
        | :? PeerChannel as p -> botApiChatId <= -1_000_000_000_000L && p.channel_id = key
        | :? PeerChat as p -> botApiChatId < 0L && botApiChatId > -1_000_000_000_000L && p.chat_id = key
        | :? PeerUser as p -> botApiChatId > 0L && p.user_id = key
        | _ -> false

    let record (msg: TL.Message) (isEdit: bool) =
        let text = if isNull msg.message then "" else msg.message
        lastActivity[struct (msg.peer_id.ID, msg.id)] <- struct (DateTime.UtcNow, text)

        if not isEdit then
            lock gate (fun () -> newMessages.Add msg)

    let onUpdates (updates: UpdatesBase) : Task =
        for sink in rawSinks do
            try
                sink updates
            with _ ->
                () // probe logging must never break the update pump

        for u in updates.UpdateList do
            match u with
            | :? UpdateNewMessage as un -> // also covers UpdateNewChannelMessage
                match un.message with
                | :? TL.Message as m -> record m false
                | _ -> ()
            | :? UpdateEditMessage as ue -> // also covers UpdateEditChannelMessage
                match ue.message with
                | :? TL.Message as m -> record m true
                | _ -> ()
            | _ -> ()

        Task.CompletedTask

    do client.add_OnUpdates (Func<UpdatesBase, Task> onUpdates)

    let snapshot () = lock gate (fun () -> newMessages.ToArray())

    let resolvePeer (botApiChatId: int64) =
        task {
            match peers.TryGetValue botApiChatId with
            | true, peer -> return peer
            | _ ->
                let! dialogs = client.Messages_GetAllDialogs()
                let key = peerKey botApiChatId

                let peer =
                    if botApiChatId < 0L then
                        match dialogs.chats.TryGetValue key with
                        | true, chat -> chat.ToInputPeer()
                        | _ -> failwith $"Chat {botApiChatId} not found in the test user's dialogs"
                    else
                        match dialogs.users.TryGetValue key with
                        | true, user -> InputPeerUser(user.id, user.access_hash) :> InputPeer
                        | _ -> failwith $"User {botApiChatId} not found in the test user's dialogs"

                peers[botApiChatId] <- peer
                return peer
        }

    /// Logged-in user, or null before LoginAsync completed.
    member _.Me = me

    /// Probe-only: registers a callback invoked with every raw update this client
    /// receives (unfiltered — includes typing/draft-action updates that `record`
    /// ignores). Handlers must not throw usefully; exceptions are swallowed.
    member _.AddRawUpdateSink(sink: UpdatesBase -> unit) = rawSinks.Add sink

    /// Resolves a public @username to a user id and caches its InputPeer, so a
    /// chat the client has no prior dialog with (e.g. a bot never messaged before)
    /// becomes usable with SendText/resolvePeer. Returns the resolved user id.
    member _.ResolveUserByUsername(username: string) : Task<int64> =
        task {
            let! resolved = client.Contacts_ResolveUsername(username, "")

            match resolved.peer with
            | :? PeerUser as pu ->
                match resolved.users.TryGetValue pu.user_id with
                | true, user ->
                    peers[pu.user_id] <- InputPeerUser(user.id, user.access_hash) :> InputPeer
                    return pu.user_id
                | _ -> return failwith $"resolved '@{username}' but its User object was missing from the response"
            | other -> return failwith $"'@{username}' resolved to a non-user peer ({other.GetType().Name})"
        }

    /// Probe-only: the most recent message ids Telegram's history API returns for
    /// `chatId` (server-side fetch, independent of the update pump) — used to check
    /// whether a draft ever materializes as a fetchable message.
    member _.RecentMessageIds(chatId: int64, limit: int) : Task<int[]> =
        task {
            let! peer = resolvePeer chatId
            let! history = client.Messages_GetHistory(peer, limit = limit)
            return history.Messages |> Array.map (fun m -> m.ID)
        }

    member _.LoginAsync() =
        task {
            let! user = client.LoginUserIfNeeded()
            me <- user
            return user
        }

    /// Sends a text message and returns its message id.
    member _.SendText(chatId: int64, text: string) : Task<int> =
        task {
            let! peer = resolvePeer chatId
            let! msg = client.SendMessageAsync(peer, text)
            return msg.id
        }

    /// Sends an on-disk ogg/opus file as a genuine voice note (not a generic audio
    /// attachment) — DocumentAttributeAudio's `voice` flag is what makes Telegram
    /// clients render it as a playable voice bubble instead of a file attachment.
    /// Returns the sent message id.
    member _.SendVoice(chatId: int64, oggFilePath: string, durationSeconds: int) : Task<int> =
        task {
            let! peer = resolvePeer chatId
            let! uploaded = client.UploadFileAsync(oggFilePath)
            let audioAttr =
                DocumentAttributeAudio(duration = durationSeconds, flags = DocumentAttributeAudio.Flags.voice)
            let media = InputMediaUploadedDocument(uploaded, "audio/ogg", [| audioAttr :> DocumentAttribute |])
            let! msg = client.SendMessageAsync(peer, "", media)
            return msg.id
        }

    /// First incoming message in `chatId` that replies to `repliedMsgId`, or None on timeout.
    member _.TryAwaitReplyTo(chatId: int64, repliedMsgId: int, timeout: TimeSpan) =
        task {
            let deadline = DateTime.UtcNow + timeout
            let mutable result = None

            while result.IsNone && DateTime.UtcNow < deadline do
                result <-
                    snapshot ()
                    |> Array.tryFind (fun m ->
                        peerMatches chatId m.peer_id
                        && (match m.ReplyTo with
                            | :? MessageReplyHeader as h -> h.reply_to_msg_id = repliedMsgId
                            | _ -> false))

                if result.IsNone then
                    do! Task.Delay pollInterval

            return result
        }

    member this.AwaitReplyTo(chatId: int64, repliedMsgId: int, timeout: TimeSpan) =
        task {
            match! this.TryAwaitReplyTo(chatId, repliedMsgId, timeout) with
            | Some m -> return m
            | None ->
                return
                    failwith $"No reply to message {repliedMsgId} in chat {chatId} within {timeout.TotalSeconds}s"
        }

    /// First incoming message from someone else in `chatId` whose text contains `marker`.
    member _.AwaitContaining(chatId: int64, marker: string, timeout: TimeSpan) =
        task {
            let deadline = DateTime.UtcNow + timeout
            let mutable result = None

            while result.IsNone && DateTime.UtcNow < deadline do
                result <-
                    snapshot ()
                    |> Array.tryFind (fun m ->
                        let fromMe =
                            match m.from_id with
                            | :? PeerUser as p -> not (isNull (box me)) && p.user_id = me.id
                            | _ -> false

                        peerMatches chatId m.peer_id
                        && not fromMe
                        && not (isNull m.message)
                        && m.message.Contains marker)

                if result.IsNone then
                    do! Task.Delay pollInterval

            match result with
            | Some m -> return m
            | None -> return failwith $"No message containing '{marker}' in chat {chatId} within {timeout.TotalSeconds}s"
        }

    /// Waits until message `msgId` has seen no new content/edits for `quietPeriod`,
    /// then returns its final text. Streaming renderers edit the message repeatedly;
    /// a single un-edited message settles after one quiet period.
    member _.AwaitEditsSettled(chatId: int64, msgId: int, quietPeriod: TimeSpan) =
        task {
            let key = struct (peerKey chatId, msgId)
            let deadline = DateTime.UtcNow + editsSettleTimeout
            let mutable settled = None

            while settled.IsNone && DateTime.UtcNow < deadline do
                match lastActivity.TryGetValue key with
                | true, struct (lastSeen, text) when DateTime.UtcNow - lastSeen >= quietPeriod -> settled <- Some text
                | _ -> do! Task.Delay pollInterval

            match settled with
            | Some text -> return text
            | None ->
                return
                    failwith
                        $"Message {msgId} in chat {chatId} never went quiet for {quietPeriod.TotalSeconds}s (waited {editsSettleTimeout.TotalSeconds}s)"
        }

    /// Human-readable dialog list with Bot API chat id conventions (-100… for channels).
    member _.ListDialogsAsync() =
        task {
            let! dialogs = client.Messages_GetAllDialogs()
            let lines = ResizeArray<string>()

            for KeyValue(_, chat) in dialogs.chats do
                let botApiId =
                    match chat with
                    | :? TL.Channel -> -1_000_000_000_000L - chat.ID
                    | _ -> -chat.ID

                lines.Add $"%20d{botApiId}  {chat.Title}"

            for KeyValue(_, user) in dialogs.users do
                lines.Add $"%20d{user.id}  {user.first_name} {user.last_name} @{user.username}"

            return List.ofSeq lines
        }

    interface IDisposable with
        member _.Dispose() = client.Dispose()

    interface IAsyncDisposable with
        member _.DisposeAsync() = client.DisposeAsync()
