namespace CouponHubBot.Services

open System
open System.Collections.Generic
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Funogram.Telegram.Types
open CouponHubBot
open CouponHubBot.Services
open CouponHubBot.Utils
open BotInfra

type CommandHandler(
    tg: ITelegramApi,
    options: IOptions<BotConfiguration>,
    db: DbService,
    notifications: TelegramNotificationService,
    couponFlow: CouponFlowHandler,
    time: TimeProvider,
    logger: ILogger<CommandHandler>
) =
    let sendText = BotHelpers.sendText tg

    // Sends the coupon's full event-history table as a <pre> HTML block, with an optional
    // header above it. Shared by /debug and /undo.
    let sendCouponHistory (chatId: int64) (couponId: int) (header: string option) =
        task {
            let! rows = db.GetCouponEventHistory(couponId)
            let table =
                if rows.Length = 0 then "Нет событий."
                else BotHelpers.formatEventHistoryTable rows
            let headerPart =
                match header with
                | Some h -> h + "\n"
                | None -> ""
            // Usernames land inside the <pre>; escape so a `<` in a name can't break the block.
            let html = $"{headerPart}<pre>{BotHelpers.htmlEscape table}</pre>"
            do! BotHelpers.sendHtml tg chatId html
        }

    // Admin-only: everything stored about a coupon (value, dates, barcode, status, owner,
    // holder) followed by its event history. No photo — the fields are what's being debugged.
    let handleDebug (userId: int64) (chatId: int64) (couponId: int) =
        task {
            if options.Value.FeedbackAdminIds |> Array.contains userId then
                match! db.GetCouponById couponId with
                | None ->
                    do! sendText chatId $"Купон ID:{couponId} не найден."
                | Some coupon ->
                    let! owner = db.GetUserById coupon.owner_id
                    let! taker =
                        task {
                            if coupon.taken_by.HasValue then
                                return! db.GetUserById coupon.taken_by.Value
                            else
                                return None
                        }
                    let details = BotHelpers.formatCouponDebugDetails coupon owner taker
                    do! sendCouponHistory chatId couponId (Some details)
            // else silently ignore for non-admins
        }

    // Admin-only rewind of the latest live action on a coupon. Replies with the post-undo
    // state plus the full event-history table (same render as /debug).
    let handleUndo (adminId: int64) (chatId: int64) (couponId: int) =
        task {
            if options.Value.FeedbackAdminIds |> Array.contains adminId then
                match! db.UndoLastEvent(couponId, adminId) with
                | UndoResult.CouponNotFound ->
                    do! sendText chatId $"Купон ID:{couponId} не найден."
                | UndoResult.NothingToUndo ->
                    do! sendText chatId $"У купона ID:{couponId} нет действий для отката."
                | UndoResult.NotUndoable "added" ->
                    do! sendText chatId "Нельзя откатить добавление купона. Используй /void."
                | UndoResult.NotUndoable other ->
                    do! sendText chatId $"Последнее действие нельзя откатить ({other})."
                | UndoResult.StateChanged ->
                    do! sendText chatId "Состояние купона изменилось, попробуй ещё раз."
                | UndoResult.Undone (coupon, revertedEvent, pocketChange) ->
                    logger.LogInformation(
                        "Admin {AdminUserId} undid '{Event}' on coupon {CouponId}; new status {Status}",
                        adminId, revertedEvent, couponId, coupon.status)
                    // An undo that moves a coupon into or out of somebody's pocket is invisible to
                    // that member unless we tell them — the admin saying "done" in a private chat is
                    // exactly the miscommunication this DM exists to close. Self-undo needs no DM:
                    // the admin is already reading the reply below.
                    let notifyPocketOwner (uid: int64) =
                        task {
                            if uid = adminId then
                                return ""
                            else
                                let! notified = notifications.NotifyUndoPocketChange(uid, coupon, revertedEvent, pocketChange)
                                return
                                    if notified then " Пользователь уведомлён."
                                    else " (⚠️ Не удалось уведомить пользователя)"
                        }
                    let! pocketLine =
                        match pocketChange with
                        | UndoPocketChange.BackToPocket uid ->
                            task {
                                let! note = notifyPocketOwner uid
                                return $"Купон снова в кармане у пользователя {uid}.{note}"
                            }
                        | UndoPocketChange.RemovedFromPocket uid ->
                            task {
                                let! note = notifyPocketOwner uid
                                return $"Купон забран из кармана пользователя {uid} и вернулся в общую копилку.{note}"
                            }
                        | UndoPocketChange.Untouched ->
                            task { return if coupon.status = "available" then "Купон снова в общей копилке." else "" }
                    let header =
                        $"Откат купона ID:{couponId}: {revertedEvent} → {coupon.status}.\n"
                        + (if pocketLine = "" then "" else pocketLine + "\n")
                        + $"Текущее состояние: {coupon.status}, до {BotHelpers.formatUiDate coupon.expires_at}."
                    do! sendCouponHistory chatId couponId (Some header)
            // else silently ignore for non-admins
        }

    let handleStart (chatId: int64) =
        sendText chatId
            "Привет! Я бот для совместного управления купонами Dunnes.\n\nКоманды:\n/add (или /a) — добавить купон\n/list (или /l) — доступные купоны\n/my (или /m) — мои купоны\n/added (или /ad) — мои добавленные\n/stats (или /s) — моя статистика\n/feedback (или /f) — фидбэк авторам\n\nДополнительно (не в меню):\n/take <id>\n/used <id>\n/return <id>\n/void <id>\n/report <id> — сообщить, что купон уже использован кем-то другим\n/help"

    let handleHelp (chatId: int64) =
        sendText chatId
            "Команды (все в личке):\n/add (/a)\n/list (/l)\n/my (/m)\n/added (/ad)\n/stats (/s)\n/feedback (/f)\n\nДополнительно:\n/take <id> (или /take для списка)\n/used <id>\n/return <id>\n/void <id>\n/report <id>\n/help"

    let handleCoupons (chatId: int64) =
        task {
            let today =
                Utils.TimeZones.dublinToday time
            let todayStr = today |> BotHelpers.formatUiDate
            let! coupons = db.GetAvailableCoupons()
            let totalStr = $"Всего доступно купонов: {coupons.Length}"
            if coupons.Length = 0 then
                do! sendText chatId $"{todayStr}\n{totalStr}\n\nСейчас нет доступных купонов."
            else
                let shown = BotHelpers.pickCouponsForList today coupons
                let text =
                    shown
                    |> Array.indexed
                    |> Array.map (fun (i, c) -> BotHelpers.formatAvailableCouponLine (i + 1) c)
                    |> String.concat "\n"
                do!
                    BotHelpers.sendTextMarkup tg chatId
                        $"{todayStr}\n{totalStr}\n\nДоступные купоны:\n{text}"
                        (BotHelpers.couponsKeyboard shown)
        }

    let handleTake (taker: DbUser) (chatId: int64) (couponId: int) =
        task {
            match! db.TryTakeCoupon(couponId, taker.id) with
            | LimitReached ->
                do!
                    let n = options.Value.MaxTakenCoupons
                    let couponWord = Utils.RussianPlural.choose n "купона" "купонов" "купонов"
                    sendText chatId
                        $"Нельзя взять больше {n} {couponWord} одновременно. Сначала верни или отметь использованным один из купонов."
            | NotFoundOrNotAvailable ->
                do! sendText chatId $"Купон ID:{couponId} уже взят или не существует."
            | Taken coupon ->
                let d = BotHelpers.formatUiDate coupon.expires_at
                do! BotHelpers.sendPhotoWithCaption tg chatId coupon.photo_file_id
                        $"Купон ID:{couponId} теперь твой: {BotHelpers.formatCouponValue coupon.value coupon.min_check}, истекает {d}"
                        (BotHelpers.singleTakenKeyboard coupon)
        }

    let handleUsed (user: DbUser) (chatId: int64) (couponId: int) =
        task {
            let! updated = db.MarkUsed(couponId, user.id)
            if updated then
                do! sendText chatId $"Купон ID:{couponId} отмечен как использованный."
            else
                do! sendText chatId $"Не получилось отметить купон ID:{couponId}. Убедись что он взят тобой."
            return updated
        }

    let handleReturn (user: DbUser) (chatId: int64) (couponId: int) =
        task {
            let! updated = db.ReturnToAvailable(couponId, user.id)
            if updated then
                do! sendText chatId $"Купон ID:{couponId} возвращён в доступные."
            else
                do! sendText chatId $"Не получилось вернуть купон ID:{couponId}. Убедись что он взят тобой."
            return updated
        }

    let handleStats (user: DbUser) (chatId: int64) =
        task {
            let! added, taken, returned, used, voided, reported = db.GetUserStats(user.id)
            let! personal = db.GetPersonalCouponOutcomes(user.id)
            let! globalStats = db.GetGlobalCouponStats()

            let fmtRate (usedN: int64) (expiredN: int64) =
                let denom = usedN + expiredN
                if denom = 0L then "—"
                else $"{int (round (float usedN / float denom * 100.0))}%%"

            let personalRate = fmtRate personal.used_count personal.expired_count
            let globalRate   = fmtRate globalStats.used_count globalStats.expired_count

            do!
                sendText chatId
                    ($"Статистика:\nДобавлено: {added} · Взято: {taken} · Возвращено: {returned} · Использовано: {used} · Аннулировано: {voided} · Пожаловался: {reported}\n\n"
                    + $"Судьба моих купонов:\n"
                    + $"Использовано: {personal.used_count}\n"
                    + $"Истекло неиспользованными: {personal.expired_count}\n"
                    + $"Сейчас активны: {personal.active_count}\n"
                    + $"Аннулировано: {personal.voided_count}\n"
                    + $"Отмечено использованными вне бота: {personal.reported_count}\n"
                    + $"Утилизация: {personalRate}\n\n"
                    + $"Сообщество (всего):\n"
                    + $"Добавлено: {globalStats.total_count}\n"
                    + $"Использовано: {globalStats.used_count}\n"
                    + $"Истекло: {globalStats.expired_count}\n"
                    + $"Активных сейчас: {globalStats.active_count}\n"
                    + $"Аннулировано: {globalStats.voided_count} · Отмечено использованными: {globalStats.reported_count}\n"
                    + $"Утилизация: {globalRate}\n\n"
                    + "ℹ️ «Аннулировано» — купон, использованный вне бота (в приложении или на сайте Dunnes). Аннулируй его в /added, чтобы он не висел в боте как доступный.")
        }

    let handleMy (user: DbUser) (chatId: int64) =
        task {
            let! taken = db.GetCouponsTakenBy(user.id)
            let! reported = db.GetReportedCouponsByOwner(user.id)
            let todayStr =
                Utils.TimeZones.dublinToday time
                |> BotHelpers.formatUiDate

            // Clamp to Telegram's media group limit of 10; always show at least 1 coupon when there are taken coupons.
            let maxShown = max 1 (min options.Value.MaxTakenCoupons 10)
            let shown = taken |> Array.truncate maxShown

            // 1) Photo(s) — SendPhoto for single item, SendMediaGroup for 2–10 (Telegram requires
            // 2–10 items in a media group). Only for held (taken) coupons; reported coupons are
            // text-only, same as /added.
            if shown.Length = 1 then
                do! BotHelpers.sendPhoto tg chatId shown[0].photo_file_id
            elif shown.Length > 1 then
                do! BotHelpers.sendMediaGroupPhotos tg chatId (shown |> Array.map (fun c -> c.photo_file_id))

            // 2) Text + inline buttons
            let truncationNote =
                if taken.Length > shown.Length then
                    $"\n(показаны первые {shown.Length} из {taken.Length})"
                else
                    ""

            let takenSection =
                if shown.Length = 0 then
                    "Мои купоны:\n—"
                else
                    let lines =
                        shown
                        |> Array.indexed
                        |> Array.map (fun (i, c) ->
                            let n = i + 1
                            let d = BotHelpers.formatUiDate c.expires_at
                            let who = BotHelpers.formatUserHandle c.owner_id c.owner_username c.owner_first_name
                            $"{n}. Купон ID:{c.id} на {BotHelpers.formatCouponValue c.value c.min_check}, до {d} · добавил {who}")
                        |> String.concat "\n"
                    "Мои купоны:\n" + lines + truncationNote

            // §4: coupons reported as used-elsewhere sit here, offering only «Использован» —
            // never «Вернуть». Returning a known-dead coupon to the pool is the exact harm
            // this feature exists to stop.
            let reportedSection =
                if reported.Length = 0 then
                    ""
                else
                    let lines =
                        reported
                        |> Array.indexed
                        |> Array.map (fun (i, c) ->
                            let n = shown.Length + i + 1
                            let d = BotHelpers.formatUiDate c.expires_at
                            $"{n}. Купон ID:{c.id} на {BotHelpers.formatCouponValue c.value c.min_check}, до {d}")
                        |> String.concat "\n"
                    "\n\n⚠️ Отмечены как использованные вне бота:\n" + lines

            let hint =
                "\n\nℹ️ Встретил уже использованный купон? Нажми «Купон уже использован» или вызови /report ID — купон вернётся владельцу."

            let kb =
                let takenRows =
                    shown
                    |> Array.map (fun c ->
                        [| InlineKeyboardButton.Create($"Вернуть ID:{c.id}", callbackData = $"return:{c.id}")
                           InlineKeyboardButton.Create($"Использован ID:{c.id}", callbackData = $"used:{c.id}") |])
                // Reported coupons offer only «Использован», never «Вернуть» — recirculating a
                // known-dead coupon is the exact harm this feature exists to stop (it previously
                // burned ~3 holders on the same dead coupon). ReportFlowTests test 5 guards this.
                let reportedRows =
                    reported
                    |> Array.map (fun c ->
                        [| InlineKeyboardButton.Create($"Использован ID:{c.id}", callbackData = $"reportedUsed:{c.id}") |])
                let bottomRows =
                    [| [| InlineKeyboardButton.Create("⚠️ Купон уже использован", callbackData = "report") |]
                       [| InlineKeyboardButton.Create("Мои добавленные", callbackData = "myAdded") |] |]
                InlineKeyboardMarkup.Create(Array.concat [ takenRows; reportedRows; bottomRows ])

            do!
                BotHelpers.sendTextMarkup tg chatId
                    $"{todayStr}\n\n{takenSection}{reportedSection}{hint}"
                    kb
        }

    let handleAdded (user: DbUser) (chatId: int64) =
        task {
            let! allCoupons = db.GetVoidableCouponsByOwner(user.id)
            if allCoupons.Length = 0 then
                do! sendText chatId "У тебя нет активных добавленных купонов."
            else
                let maxShown = 20
                let coupons = allCoupons |> Array.truncate maxShown
                let remaining = allCoupons.Length - coupons.Length
                let text =
                    let lines =
                        coupons
                        |> Array.indexed
                        |> Array.map (fun (i, c) ->
                            let n = i + 1
                            let d = BotHelpers.formatUiDate c.expires_at
                            let barcodeSuffix =
                                if String.IsNullOrEmpty(c.barcode_text) || c.barcode_text.Length < 4 then ""
                                else $" ···{c.barcode_text[c.barcode_text.Length - 4 ..]}"
                            let statusText =
                                match c.status with
                                | "taken" -> " (взят)"
                                | "reported" -> " (отмечен использованным)"
                                | _ -> ""
                            $"{n}. ID:{c.id} — {BotHelpers.formatCouponValue c.value c.min_check}, {d}{barcodeSuffix}{statusText}")
                        |> String.concat "\n"
                    if remaining > 0 then
                        lines + $"\n...и ещё {remaining} купонов"
                    else
                        lines

                let kb =
                    coupons
                    |> Array.map (fun c ->
                        [| InlineKeyboardButton.Create($"Аннулировать ID:{c.id}", callbackData = $"void:{c.id}") |])
                    |> fun rows -> InlineKeyboardMarkup.Create(rows)

                do! BotHelpers.sendTextMarkup tg chatId $"Мои добавленные купоны:\n{text}" kb
        }

    let handleVoid (user: DbUser) (chatId: int64) (couponId: int) (isAdmin: bool) (deleteMsg: bool) (msgIdToDelete: int64 option) =
        task {
            match! db.VoidCoupon(couponId, user.id, isAdmin) with
            | VoidCouponResult.NotFoundOrNotAllowed ->
                do! sendText chatId $"Не удалось аннулировать купон ID:{couponId}. Убедись, что он не истёк и не использован."
            | VoidCouponResult.Voided (coupon, takenBy) ->
                if isAdmin && coupon.owner_id <> user.id then
                    logger.LogInformation("Admin {AdminUserId} voided coupon {CouponId} owned by {OwnerId}", user.id, couponId, coupon.owner_id)
                let! notifyWarning =
                    match takenBy with
                    | Some takerId ->
                        task {
                            let! notified = notifications.NotifyTakerCouponVoided(takerId, coupon)
                            return if not notified then " (⚠️ Не удалось уведомить того, кто взял купон)" else ""
                        }
                    | None -> task { return "" }
                let confirmText = $"Купон ID:{couponId} аннулирован.{notifyWarning}"
                do! sendText chatId confirmText
                if deleteMsg then
                    match msgIdToDelete with
                    | Some msgId ->
                        try
                            do! BotHelpers.deleteMessage tg chatId msgId
                        with _ -> ()
                    | None -> ()
        }

    // Holder reports a coupon as already used externally (taken -> reported). Authorization,
    // event bookkeeping and taken_by/taken_at clearing all live in DbService.TryReportCoupon;
    // this just renders the three outcomes and fires the adder notification (§1, §2, §6).
    let handleReport (user: DbUser) (chatId: int64) (couponId: int) =
        task {
            match! db.TryReportCoupon(couponId, user.id) with
            | ReportCouponResult.NotFound ->
                do! sendText chatId "Купон не найден."
            | ReportCouponResult.NotActive ->
                do! sendText chatId "Купон уже не активен."
            | ReportCouponResult.NotHolder ->
                do! sendText chatId "Этот купон не у тебя."
            | ReportCouponResult.Reported coupon ->
                logger.LogInformation("User {ReporterId} reported coupon {CouponId} owned by {OwnerId} as used externally", user.id, couponId, coupon.owner_id)
                let! ownerOpt = db.GetUserById(coupon.owner_id)
                let ownerHandle =
                    match ownerOpt with
                    | Some owner -> BotHelpers.formatUserHandle owner.id owner.username owner.first_name
                    | None -> string coupon.owner_id
                let reporterHandle = BotHelpers.formatUserHandle user.id user.username user.first_name
                let! notified = notifications.NotifyAdderCouponReported(coupon.owner_id, coupon, reporterHandle)
                let notifyWarning = if not notified then " (⚠️ Не удалось уведомить владельца купона)" else ""
                do! sendText chatId $"Спасибо! Купон ID:{couponId} отправлен владельцу {ownerHandle}. Он больше не в общем пуле.{notifyWarning}"
        }

    // Adder's own path from 'reported' -> 'used' (§4), distinct from handleUsed (taker-only, 'taken' -> 'used').
    let handleReportedUsed (user: DbUser) (chatId: int64) (couponId: int) =
        task {
            let! updated = db.MarkReportedUsed(couponId, user.id)
            if updated then
                do! sendText chatId $"Купон ID:{couponId} отмечен как использованный."
            else
                do! sendText chatId $"Не получилось отметить купон ID:{couponId}."
            return updated
        }

    let handleFeedback (user: DbUser) (chatId: int64) =
        task {
            if options.Value.FeedbackAdminIds.Length = 0 then
                do! sendText chatId "Фидбэк пока не настроен (нет админов)."
            else
                do! db.SetPendingFeedback(user.id)
                do!
                    sendText chatId
                        "Следующее твоё сообщение в этом чате (в любом виде: текст, фото, голосовое и т.д.) я отправлю моим авторам. Если передумаешь — просто введи любую команду (например /help)."
        }

    member _.HandleTake (taker: DbUser) (chatId: int64) (couponId: int) = handleTake taker chatId couponId
    member _.HandleReturn (user: DbUser) (chatId: int64) (couponId: int) = handleReturn user chatId couponId
    member _.HandleUsed (user: DbUser) (chatId: int64) (couponId: int) = handleUsed user chatId couponId
    member _.HandleVoid (user: DbUser) (chatId: int64) (couponId: int) (isAdmin: bool) (deleteMsg: bool) (msgIdToDelete: int64 option) = handleVoid user chatId couponId isAdmin deleteMsg msgIdToDelete
    member _.HandleReport (user: DbUser) (chatId: int64) (couponId: int) = handleReport user chatId couponId
    member _.HandleReportedUsed (user: DbUser) (chatId: int64) (couponId: int) = handleReportedUsed user chatId couponId
    member _.HandleAdded (user: DbUser) (chatId: int64) = handleAdded user chatId
    member _.HandleUndo (adminId: int64) (chatId: int64) (couponId: int) = handleUndo adminId chatId couponId

    member _.Dispatch (user: DbUser) (msg: Message) =
        task {
            let recordCommand cmd =
                Metrics.commandTotal.Add(1L, KeyValuePair("command", box cmd))

            // user.id always equals msg.From.Id here: BotService only dispatches private
            // messages whose From is present, and `user` is upserted from that From.
            match msg.Text with
            | Some "/start" ->
                recordCommand "start"
                do! handleStart msg.Chat.Id
            | Some "/help" ->
                recordCommand "help"
                do! handleHelp msg.Chat.Id
            | Some "/list" ->
                recordCommand "list"
                do! handleCoupons msg.Chat.Id
            | Some "/l" ->
                recordCommand "list"
                do! handleCoupons msg.Chat.Id
            | Some "/coupons" ->
                recordCommand "list"
                do! handleCoupons msg.Chat.Id // legacy alias
            | Some "/take" ->
                recordCommand "list"
                do! handleCoupons msg.Chat.Id // legacy alias (list)
            | Some "/my" ->
                recordCommand "my"
                do! handleMy user msg.Chat.Id
            | Some "/m" ->
                recordCommand "my"
                do! handleMy user msg.Chat.Id
            | Some "/added" ->
                recordCommand "added"
                do! handleAdded user msg.Chat.Id
            | Some "/ad" ->
                recordCommand "added"
                do! handleAdded user msg.Chat.Id
            | Some "/stats" ->
                recordCommand "stats"
                do! handleStats user msg.Chat.Id
            | Some "/s" ->
                recordCommand "stats"
                do! handleStats user msg.Chat.Id
            | Some "/feedback" ->
                recordCommand "feedback"
                do! handleFeedback user msg.Chat.Id
            | Some "/f" ->
                recordCommand "feedback"
                do! handleFeedback user msg.Chat.Id
            | Some "/add" ->
                recordCommand "add"
                do! couponFlow.HandleAddWizardStart user msg.Chat.Id
            | Some "/a" ->
                recordCommand "add"
                do! couponFlow.HandleAddWizardStart user msg.Chat.Id
            | Some t when t.StartsWith("/take ") ->
                recordCommand "take"
                match t.Split([|' '|], System.StringSplitOptions.RemoveEmptyEntries) |> Array.tryLast |> Option.bind BotHelpers.parseInt with
                | Some couponId -> do! handleTake user msg.Chat.Id couponId
                | None -> do! sendText msg.Chat.Id "Формат: /take <id>"
            | Some t when t.StartsWith("/used ") ->
                recordCommand "used"
                match t.Split([|' '|], System.StringSplitOptions.RemoveEmptyEntries) |> Array.tryLast |> Option.bind BotHelpers.parseInt with
                | Some couponId ->
                    let! _ = handleUsed user msg.Chat.Id couponId
                    ()
                | None -> do! sendText msg.Chat.Id "Формат: /used <id>"
            | Some t when t.StartsWith("/return ") ->
                recordCommand "return"
                match t.Split([|' '|], System.StringSplitOptions.RemoveEmptyEntries) |> Array.tryLast |> Option.bind BotHelpers.parseInt with
                | Some couponId ->
                    let! _ = handleReturn user msg.Chat.Id couponId
                    ()
                | None -> do! sendText msg.Chat.Id "Формат: /return <id>"
            | Some t when t.StartsWith("/add ") || t.StartsWith("/a ") ->
                recordCommand "add_manual"
                do! sendText msg.Chat.Id "Для ручного добавления пришли фото с подписью: /add <discount> <min_check> <date>"
            | Some t when t.StartsWith("/void ") ->
                recordCommand "void"
                match t.Split([|' '|], System.StringSplitOptions.RemoveEmptyEntries) |> Array.tryLast |> Option.bind BotHelpers.parseInt with
                | Some couponId ->
                    let isAdmin = options.Value.FeedbackAdminIds |> Array.contains user.id
                    do! handleVoid user msg.Chat.Id couponId isAdmin false None
                | None -> do! sendText msg.Chat.Id "Формат: /void <id>"
            | Some t when t.StartsWith("/report ") ->
                recordCommand "report"
                match t.Split([|' '|], System.StringSplitOptions.RemoveEmptyEntries) |> Array.tryLast |> Option.bind BotHelpers.parseInt with
                | Some couponId -> do! handleReport user msg.Chat.Id couponId
                | None -> do! sendText msg.Chat.Id "Формат: /report <id>"
            | Some t when t.StartsWith("/debug ") ->
                recordCommand "debug"
                match t.Split([|' '|], System.StringSplitOptions.RemoveEmptyEntries) |> Array.tryLast |> Option.bind BotHelpers.parseInt with
                | Some couponId -> do! handleDebug user.id msg.Chat.Id couponId
                | None -> ()
            | Some t when t.StartsWith("/undo ") ->
                recordCommand "undo"
                match t.Split([|' '|], System.StringSplitOptions.RemoveEmptyEntries) |> Array.tryLast |> Option.bind BotHelpers.parseInt with
                | Some couponId -> do! handleUndo user.id msg.Chat.Id couponId
                | None -> ()
            | _ ->
                let hasPhoto = msg.Photo |> Option.exists (fun p -> p.Length > 0)
                let captionIsAdd =
                    msg.Caption |> Option.exists (fun c -> c.StartsWith("/add") || c.StartsWith("/a"))
                if hasPhoto && captionIsAdd then
                    recordCommand "add_photo"
                    do! couponFlow.HandleAddManual user msg
                else
                    logger.LogInformation("Ignoring private message")
        }
