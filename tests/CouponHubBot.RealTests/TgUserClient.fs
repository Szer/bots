namespace CouponHubBot.RealTests

open System
open System.Collections.Concurrent
open System.Text
open System.Threading.Tasks
open TL

/// COPIED from tests/AlitaBot.RealTests/TgUserClient.fs (contract: "Reuse, do not
/// rewrite" names this file bot-agnostic and suggests moving it to a shared project;
/// moving it would force every consuming file in AlitaBot.RealTests to add an `open`
/// for the new namespace — a change "inside AlitaBot.RealTests" the contract says to
/// avoid. Copying instead, per the contract's own fallback instruction). Trimmed to
/// what CouponHubBot's DM-only, non-streaming command/callback surface actually needs
/// (dropped Alita's voice/vision/draft/reaction-probe members), and extended with
/// `SendAlbum` (album/bulk-add real test) and the `AfterMsgId`-gated Await* family
/// (see below) that CouponHubBot's fixed, template-driven Russian reply text needs but
/// Alita's marker/GUID-in-text LLM replies never did.
///
/// Raised by the Await* helpers when their poll deadline elapses without a match. This
/// is the ONLY exception TestRetry.withTimeoutRetry treats as retryable — genuine
/// assertion failures (Assert.*, Assert.Fail) are a different, non-flaky failure mode.
type AwaitTimeoutException(message: string) =
    inherit Exception(message)

/// MTProto user client (WTelegramClient) playing the human in the test group.
/// Incoming updates are recorded and polled every 500ms by the Await* helpers.
/// Bot API supergroup ids (-100xxxxxxxxxx) are translated to MTProto channel
/// ids; access hashes are resolved via Messages_GetAllDialogs and cached.
type TgUserClient(apiId: string, apiHash: string, sessionPath: string, phone: string, ?prompt: string -> string) =

    static let pollInterval = TimeSpan.FromMilliseconds 500.

    /// Non-interactive by default: tests must never block on stdin.
    let prompt =
        defaultArg prompt (fun key ->
            failwith $"WTelegram asked for '{key}' — interactive login required, run `make coupon-tg-login` first")

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

    /// Bot API chat id -> resolved InputPeer (with access hash).
    let peers = ConcurrentDictionary<int64, InputPeer>()

    let mutable me: TL.User = Unchecked.defaultof<TL.User>

    /// MTProto peer id for a Bot API chat id: -100xxxxxxxxxx -> xxxxxxxxxx (channel),
    /// -yyy -> yyy (basic group), positive ids unchanged (user/private chat — every
    /// CouponHubBot command/callback exchange is this last case).
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
        if not isEdit then
            lock gate (fun () -> newMessages.Add msg)

    let onUpdates (updates: UpdatesBase) : Task =
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

    /// Poll helper shared by every Await*/TryAwait* below: first recorded message in
    /// `chatId` with MTProto id > `afterMsgId` matching `predicate`. `afterMsgId` gates
    /// out cross-test staleness in this single long-lived private DM (unlike Alita's
    /// GUID-marker-in-text approach — CouponHubBot's replies are fixed Russian template
    /// text, e.g. "/start" always replies with the same "Привет..." string, so an earlier
    /// test's identical reply sitting in the buffer could otherwise satisfy a later
    /// test's AwaitTextContaining immediately, without ever waiting for a fresh one).
    /// Pass the id returned by the SendText/SendPhoto/SendAlbum call that triggered the
    /// expected reply as `afterMsgId` (0 to consider every recorded message).
    let tryPollAfter (chatId: int64) (afterMsgId: int) (predicate: TL.Message -> bool) (timeout: TimeSpan) =
        task {
            let deadline = DateTime.UtcNow + timeout
            let mutable result = None

            while result.IsNone && DateTime.UtcNow < deadline do
                result <-
                    snapshot ()
                    |> Array.tryFind (fun m -> peerMatches chatId m.peer_id && m.id > afterMsgId && predicate m)

                if result.IsNone then
                    do! Task.Delay pollInterval

            return result
        }

    let pollAfter (chatId: int64) (afterMsgId: int) (predicate: TL.Message -> bool) (timeout: TimeSpan) (description: string) =
        task {
            match! tryPollAfter chatId afterMsgId predicate timeout with
            | Some m -> return m
            | None ->
                return
                    raise (
                        AwaitTimeoutException
                            $"No message matching '{description}' in chat {chatId} (after msg {afterMsgId}) within {timeout.TotalSeconds}s")
        }

    /// Logged-in user, or null before LoginAsync completed.
    member _.Me = me

    /// Resolves a public @username to a user id and caches its InputPeer, so a
    /// chat the client has no prior dialog with (e.g. the test bot, never
    /// messaged before) becomes usable with SendText/resolvePeer. Returns the
    /// resolved user id — this doubles as the Bot-API-style chat id for the
    /// resulting private chat (CouponHubBot's commands are all DMs).
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

    /// Sends an on-disk image file as a photo with a caption. Used by the /add real
    /// test (photo + "/add <value> <minCheck> <date>" caption) and by /take's
    /// photo-with-caption confirmation detection. Returns the sent message id.
    member _.SendPhoto(chatId: int64, imagePath: string, caption: string) : Task<int> =
        task {
            let! peer = resolvePeer chatId
            let! uploaded = client.UploadFileAsync(imagePath)
            let media = InputMediaUploadedPhoto(file = uploaded)
            let! msg = client.SendMessageAsync(peer, caption, media)
            return msg.id
        }

    /// Sends several on-disk image files as ONE Telegram album (shared grouped_id ->
    /// Bot API's media_group_id) — the bulk/album-add real test's trigger. Returns the
    /// highest message id among the album's messages (a safe "afterMsgId" baseline for
    /// the ensuing Await* call, since Telegram assigns album messages consecutive ids).
    member _.SendAlbum(chatId: int64, imagePaths: string list) : Task<int> =
        task {
            let! peer = resolvePeer chatId
            let! uploadedFiles = imagePaths |> List.map client.UploadFileAsync |> Task.WhenAll
            let medias =
                uploadedFiles
                |> Array.map (fun f -> InputMediaUploadedPhoto(file = f) :> InputMedia)
                :> System.Collections.Generic.ICollection<InputMedia>
            let! sent = client.SendAlbumAsync(peer, medias)
            return sent |> Array.map (fun m -> m.id) |> Array.max
        }

    /// First message from the bot in `chatId` (id > `afterMsgId`) whose text contains
    /// `marker`. Non-throwing — used where "the bot stayed silent" is itself the assertion.
    member _.TryAwaitTextContaining(chatId: int64, afterMsgId: int, marker: string, timeout: TimeSpan) =
        tryPollAfter chatId afterMsgId (fun m -> not (isNull m.message) && m.message.Contains marker) timeout

    member this.AwaitTextContaining(chatId: int64, afterMsgId: int, marker: string, timeout: TimeSpan) =
        pollAfter chatId afterMsgId (fun m -> not (isNull m.message) && m.message.Contains marker) timeout $"text containing '{marker}'"

    /// First message from the bot in `chatId` (id > `afterMsgId`) carrying a photo whose
    /// caption contains `marker` — used for /take's "теперь твой" confirmation, which
    /// CouponHubBot sends as sendPhoto+caption rather than a plain text message.
    member this.AwaitPhotoCaptionContaining(chatId: int64, afterMsgId: int, marker: string, timeout: TimeSpan) =
        pollAfter
            chatId
            afterMsgId
            (fun m ->
                (match m.media with
                 | :? MessageMediaPhoto -> true
                 | _ -> false)
                && not (isNull m.message)
                && m.message.Contains marker)
            timeout
            $"photo caption containing '{marker}'"

    /// Reads the callback_data (UTF8-decoded) of the first inline button on `msg`
    /// whose data satisfies `predicate` — e.g. the "Подтвердить N купонов" bulk-add
    /// button's `addflow:bulk:confirm:<batchId>` payload. Real Telegram clients read
    /// the button's own data rather than knowing the batch id out of band, so this
    /// mirrors that (and sidesteps needing a DB lookup for the id).
    member _.FindCallbackData(msg: TL.Message, predicate: string -> bool) : string option =
        match msg.reply_markup with
        | :? ReplyInlineMarkup as markup ->
            markup.rows
            |> Array.collect (fun r -> r.buttons)
            |> Array.tryPick (fun b ->
                match b with
                | :? KeyboardButtonCallback as cb ->
                    let data = Encoding.UTF8.GetString cb.data
                    if predicate data then Some data else None
                | _ -> None)
        | _ -> None

    /// Presses an inline keyboard button — the MTProto equivalent of a Telegram
    /// client tapping a button under `msgId`, which is what actually delivers a
    /// callback_query update to the bot (Messages_GetBotCallbackAnswer both notifies
    /// the bot AND returns the "answered" toast/alert, mirroring what a real client
    /// does; there is no lighter-weight MTProto primitive for this). Required for
    /// coverage item 3 (bulk/album add's addflow:bulk:confirm callback) — every OTHER
    /// callback this suite touches (take/used/return/report) has a plain "/cmd <id>"
    /// text-command equivalent (see CommandHandler.fs's Dispatch), so this is used
    /// sparingly.
    member _.PressCallbackButton(chatId: int64, msgId: int, callbackData: string) : Task =
        task {
            let! peer = resolvePeer chatId
            // Use the SchemaExtensions helper, not a bare `TL.Methods.Messages_GetBotCallbackAnswer`
            // object initializer: that raw request type has a `flags` field that gates
            // whether `data` is written to the wire at all (WriteTL only serializes
            // `data` when the `has_data` bit is set), and object-initializer syntax
            // does NOT set it — every press silently went out with no data attached,
            // which Telegram rejects with DATA_INVALID. The extension method computes
            // `flags` from which optional args are non-null.
            let! _answer = client.Messages_GetBotCallbackAnswer(peer, msgId, data = Encoding.UTF8.GetBytes callbackData)
            ()
        }
        :> Task

    /// FindCallbackData + PressCallbackButton in one call — nearly every wizard/callback
    /// step in the real suite is exactly that pair (see CouponLifecycleRealTests.fs and
    /// BulkAddRealTests.fs, which both do it by hand today). Unlike a bare
    /// `FindCallbackData` miss (currently surfaced per call site as
    /// `Assert.True(data.IsSome, "Expected a foo:X button")`, which says nothing about
    /// what buttons WERE on the message), a miss here raises with the full list of
    /// callback_data actually present, so a wrong-button failure is self-explanatory
    /// without a debugger. Deliberately NOT an AwaitTimeoutException: `msg` already
    /// arrived, so a missing button is a real behavioral assertion about the bot's
    /// reply, not flakiness — TestRetry.withTimeoutRetry does not catch this (see its
    /// own doc comment on why assertion failures must not be retried).
    member this.PressCallbackButtonMatching(chatId: int64, msg: TL.Message, predicate: string -> bool, description: string) : Task =
        task {
            match this.FindCallbackData(msg, predicate) with
            | Some data -> do! this.PressCallbackButton(chatId, msg.id, data)
            | None ->
                let present =
                    match msg.reply_markup with
                    | :? ReplyInlineMarkup as markup ->
                        markup.rows
                        |> Array.collect (fun r -> r.buttons)
                        |> Array.choose (fun b ->
                            match b with
                            | :? KeyboardButtonCallback as cb -> Some(Encoding.UTF8.GetString cb.data)
                            | _ -> None)
                        |> String.concat ", "
                    | _ -> "<no inline keyboard on this message>"

                failwith
                    $"No button matching '{description}' on message {msg.id} in chat {chatId}. Callback data present: [{present}]"
        }
        :> Task

    /// AwaitTextContaining + PressCallbackButtonMatching in one call — the shape of
    /// nearly every step in the expansion suite's wizard/callback chains ("wait for the
    /// next prompt, tap the button that advances it"). The await half can raise
    /// AwaitTimeoutException (retryable, per TestRetry); the press half raises via
    /// PressCallbackButtonMatching above (not retryable) — callers get both failure
    /// modes for free without re-deriving which is which at each call site.
    member this.AwaitAndPressButton
        (
            chatId: int64,
            afterMsgId: int,
            textMarker: string,
            timeout: TimeSpan,
            buttonPredicate: string -> bool,
            buttonDescription: string
        ) : Task<TL.Message> =
        task {
            let! msg = this.AwaitTextContaining(chatId, afterMsgId, textMarker, timeout)
            do! this.PressCallbackButtonMatching(chatId, msg, buttonPredicate, buttonDescription)
            return msg
        }

    /// Human-readable dialog list with Bot API chat id conventions (-100… for channels)
    /// — `make coupon-tg-chats` uses this to find the CI group id.
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
