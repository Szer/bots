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

    /// (peer id, message id) -> latest entities (Slice 6 MDV2 real-test: best-effort
    /// check that the settled final message actually carries bold/etc. entities, not
    /// just plain text) — a message with no entities on a given update stores `[||]`,
    /// same as Telegram's own `null`/empty-array convention.
    let lastEntities = ConcurrentDictionary<struct (int64 * int), MessageEntity[]>()

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
        lastEntities[struct (msg.peer_id.ID, msg.id)] <- (if isNull msg.entities then [||] else msg.entities)

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

    /// Sends an on-disk image file as a photo with a caption (used by VisionRealTests to
    /// exercise the vision feature — the caption carries the "@bot <question> <guid>" trigger).
    /// Returns the sent message id.
    member _.SendPhoto(chatId: int64, imagePath: string, caption: string) : Task<int> =
        task {
            let! peer = resolvePeer chatId
            let! uploaded = client.UploadFileAsync(imagePath)
            let media = InputMediaUploadedPhoto(file = uploaded)
            let! msg = client.SendMessageAsync(peer, caption, media)
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

    /// First incoming message in `chatId` that replies to `repliedMsgId` AND carries a photo
    /// (TL.MessageMediaPhoto) — used by ImageGenRealTests to detect the bot's generated-image
    /// reply, which never arrives as a plain text message so AwaitReplyTo alone can't see it.
    member _.TryAwaitPhotoReplyTo(chatId: int64, repliedMsgId: int, timeout: TimeSpan) =
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
                            | _ -> false)
                        && (match m.media with
                            | :? MessageMediaPhoto -> true
                            | _ -> false))

                if result.IsNone then
                    do! Task.Delay pollInterval

            return result
        }

    member this.AwaitPhotoReplyTo(chatId: int64, repliedMsgId: int, timeout: TimeSpan) =
        task {
            match! this.TryAwaitPhotoReplyTo(chatId, repliedMsgId, timeout) with
            | Some m -> return m
            | None ->
                return
                    failwith $"No photo reply to message {repliedMsgId} in chat {chatId} within {timeout.TotalSeconds}s"
        }

    /// First incoming message in `chatId` that replies to `repliedMsgId` AND carries a
    /// genuine voice note (TL.MessageMediaDocument whose document has a
    /// DocumentAttributeAudio with the `voice` flag set) — used by StretchRealTests to
    /// detect `/say`'s voice-note reply, the same way TryAwaitPhotoReplyTo detects /img's
    /// photo reply. Returns (durationSeconds, byteSize) on the matched message.
    member _.TryAwaitVoiceReplyTo(chatId: int64, repliedMsgId: int, timeout: TimeSpan) : Task<(int * int64) option> =
        task {
            let deadline = DateTime.UtcNow + timeout
            let mutable result = None

            while result.IsNone && DateTime.UtcNow < deadline do
                result <-
                    snapshot ()
                    |> Array.tryPick (fun m ->
                        if
                            peerMatches chatId m.peer_id
                            && (match m.ReplyTo with
                                | :? MessageReplyHeader as h -> h.reply_to_msg_id = repliedMsgId
                                | _ -> false)
                        then
                            match m.media with
                            | :? MessageMediaDocument as md ->
                                match md.document with
                                | :? TL.Document as doc ->
                                    doc.attributes
                                    |> Array.tryPick (fun a ->
                                        match a with
                                        | :? DocumentAttributeAudio as aa when aa.flags.HasFlag(DocumentAttributeAudio.Flags.voice) ->
                                            Some(aa.duration, doc.size)
                                        | _ -> None)
                                | _ -> None
                            | _ -> None
                        else
                            None)

                if result.IsNone then
                    do! Task.Delay pollInterval

            return result
        }

    member this.AwaitVoiceReplyTo(chatId: int64, repliedMsgId: int, timeout: TimeSpan) =
        task {
            match! this.TryAwaitVoiceReplyTo(chatId, repliedMsgId, timeout) with
            | Some r -> return r
            | None ->
                return
                    failwith $"No voice-note reply to message {repliedMsgId} in chat {chatId} within {timeout.TotalSeconds}s"
        }

    /// Same shape as TryAwaitVoiceReplyTo, but for a REGULAR audio attachment (Bot API
    /// `sendAudio`, no `voice` flag on the DocumentAttributeAudio) — used by SongRealTests
    /// to detect `/song`'s Lyria-generated track. Returns (durationSeconds, byteSize).
    member _.TryAwaitAudioReplyTo(chatId: int64, repliedMsgId: int, timeout: TimeSpan) : Task<(int * int64) option> =
        task {
            let deadline = DateTime.UtcNow + timeout
            let mutable result = None

            while result.IsNone && DateTime.UtcNow < deadline do
                result <-
                    snapshot ()
                    |> Array.tryPick (fun m ->
                        if
                            peerMatches chatId m.peer_id
                            && (match m.ReplyTo with
                                | :? MessageReplyHeader as h -> h.reply_to_msg_id = repliedMsgId
                                | _ -> false)
                        then
                            match m.media with
                            | :? MessageMediaDocument as md ->
                                match md.document with
                                | :? TL.Document as doc ->
                                    doc.attributes
                                    |> Array.tryPick (fun a ->
                                        match a with
                                        | :? DocumentAttributeAudio as aa -> Some(aa.duration, doc.size)
                                        | _ -> None)
                                | _ -> None
                            | _ -> None
                        else
                            None)

                if result.IsNone then
                    do! Task.Delay pollInterval

            return result
        }

    member this.AwaitAudioReplyTo(chatId: int64, repliedMsgId: int, timeout: TimeSpan) =
        task {
            match! this.TryAwaitAudioReplyTo(chatId, repliedMsgId, timeout) with
            | Some r -> return r
            | None ->
                return
                    failwith $"No audio reply to message {repliedMsgId} in chat {chatId} within {timeout.TotalSeconds}s"
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

    /// Probe-only (Slice 6 outcome-router real test): polls `Messages_GetHistory` until
    /// `msgId` in `chatId` carries at least one reaction, or `timeout` elapses.
    ///
    /// EMPIRICAL FINDING (M6/Slice 6, this repo's basic-group test chat): a bot's
    /// `setMessageReaction` (Bot API) genuinely lands server-side — confirmed by fetching
    /// history moments later and finding `Message.reactions.results` populated — but the
    /// corresponding `UpdateMessageReactions` this harness's raw-update sink expected
    /// (the update shape a REGULAR MTProto client is documented to receive for a
    /// reaction change; bots get a different, bot-only pair —
    /// `UpdateBotMessageReaction`/`UpdateBotMessageReactions`, delivered solely to their
    /// own getUpdates/webhook, never to this harness) was never observed to arrive over
    /// this client's live update stream within 30s, even though every other live-update
    /// path this harness relies on (`UpdateNewMessage`/`UpdateEditMessage`, used by
    /// `AwaitReplyTo`/`AwaitEditsSettled` throughout every other real test) works
    /// reliably in the same chat. Whether that's a basic-group-specific gap (same shape
    /// as `sendMessageDraft`'s `TEXTDRAFT_PEER_INVALID` and the ephemeral-message
    /// `BOT_NOT_ADMIN` findings elsewhere in this README) or something specific to a
    /// bot-authored reaction is unconfirmed — polling the authoritative source
    /// (`Messages_GetHistory`) sidesteps the question rather than depending on a push
    /// update this harness can't reliably observe.
    member _.TryAwaitReactionOn(chatId: int64, msgId: int, timeout: TimeSpan) : Task<string[] option> =
        task {
            let! peer = resolvePeer chatId
            let deadline = DateTime.UtcNow + timeout
            let mutable result = None

            while result.IsNone && DateTime.UtcNow < deadline do
                let! history = client.Messages_GetHistory(peer, limit = 30)

                let found =
                    history.Messages
                    |> Array.tryPick (fun m ->
                        match m with
                        | :? TL.Message as msg when msg.id = msgId ->
                            if isNull (box msg.reactions) || isNull msg.reactions.results || msg.reactions.results.Length = 0 then
                                None
                            else
                                Some(
                                    msg.reactions.results
                                    |> Array.choose (fun rc ->
                                        match rc.reaction with
                                        | :? ReactionEmoji as re -> Some re.emoticon
                                        | _ -> None)
                                )
                        | _ -> None)

                match found with
                | Some e -> result <- Some e
                | None -> do! Task.Delay pollInterval

            return result
        }

    /// Entities recorded on the latest new/edit seen for `msgId` in `chatId` — read this
    /// right after `AwaitEditsSettled` confirms the message has gone quiet, so it
    /// reflects the settled, final content (Slice 6 MDV2 real-test). `[||]` for "settled
    /// but no entities" as well as "nothing recorded yet" — callers that need to
    /// distinguish those should call `AwaitEditsSettled` first.
    member _.LastEntitiesOf(chatId: int64, msgId: int) : MessageEntity[] =
        match lastEntities.TryGetValue(struct (peerKey chatId, msgId)) with
        | true, e -> e
        | _ -> [||]

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
