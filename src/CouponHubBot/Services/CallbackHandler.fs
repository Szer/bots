namespace CouponHubBot.Services

open System
open System.Collections.Generic
open System.Diagnostics
open System.Runtime.ExceptionServices
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Funogram.Telegram.Types
open CouponHubBot
open CouponHubBot.Services
open CouponHubBot.Telemetry
open CouponHubBot.Utils
open BotInfra

type CallbackHandler(
    tg: ITelegramApi,
    options: IOptions<BotConfiguration>,
    db: DbService,
    membership: TelegramMembershipService,
    couponFlow: CouponFlowHandler,
    commandHandler: CommandHandler,
    time: TimeProvider,
    logger: ILogger<CallbackHandler>
) =
    let sendText = BotHelpers.sendText tg
    let ensureCommunityMember = BotHelpers.ensureCommunityMember membership sendText

    member private _.EditBulkOrSend (batch: PendingAddBatch) (text: string) =
        task {
            if batch.bulk_message_id.HasValue then
                try
                    do! BotHelpers.editMessageText tg batch.bulk_chat_id batch.bulk_message_id.Value text
                with _ ->
                    do! sendText batch.bulk_chat_id text
            else
                do! sendText batch.bulk_chat_id text
        }

    member private this.BulkBatchCancel (batch: PendingAddBatch) =
        task {
            let! items = db.GetBatchItems batch.id
            let hadOkItems = items |> Array.exists (fun i -> i.status = "ok")
            Metrics.batchCancelTotal.Add(
                1L,
                KeyValuePair("had_ok_items", box (if hadOkItems then "true" else "false")))
            logger.LogInformation(
                "Batch {BatchId} cancelled by user {UserId} (had_ok_items={HadOkItems})",
                batch.id, batch.user_id, hadOkItems)
            do! db.ClearBatch batch.id
            do! this.EditBulkOrSend batch "Ок, пакет отменён."
        }

    member private this.BulkBatchConfirm (user: DbUser) (batch: PendingAddBatch) =
        task {
            Metrics.batchConfirmTotal.Add(1L)
            let! items = db.GetBatchItems batch.id
            let okItems = items |> Array.filter (fun i -> i.status = "ok")
            let insertedIds = ResizeArray<int>()
            let skippedNotes = ResizeArray<string>()

            for item in okItems do
                let vf =
                    if item.valid_from.HasValue then Some item.valid_from.Value
                    else None
                let! result =
                    db.TryAddCoupon(
                        user.id,
                        item.photo_file_id,
                        item.value.Value,
                        item.min_check.Value,
                        item.expires_at.Value,
                        item.barcode_text,
                        ?validFrom = vf)
                match result with
                | AddCouponResult.Added c ->
                    insertedIds.Add c.id
                    Metrics.batchAddedTotal.Add(1L)
                    do! db.MarkBatchItemInserted(item.id, c.id)
                | AddCouponResult.Expired ->
                    skippedNotes.Add "истёкший купон"
                    Metrics.batchSkippedTotal.Add(1L, KeyValuePair("reason", box "Expired"))
                    do! db.MarkBatchItemFailed(item.id, "expired")
                | AddCouponResult.DuplicatePhoto existingId ->
                    skippedNotes.Add $"дубликат фото (ID:{existingId})"
                    Metrics.batchSkippedTotal.Add(1L, KeyValuePair("reason", box "DuplicatePhoto"))
                    do! db.MarkBatchItemFailed(item.id, $"dup_photo:{existingId}")
                | AddCouponResult.DuplicateBarcode existingId ->
                    skippedNotes.Add $"дубликат штрихкода (ID:{existingId})"
                    Metrics.batchSkippedTotal.Add(1L, KeyValuePair("reason", box "DuplicateBarcode"))
                    do! db.MarkBatchItemFailed(item.id, $"dup_barcode:{existingId}")

            logger.LogInformation(
                "Batch {BatchId} confirmed by user {UserId}: added={Added} skipped={Skipped}",
                batch.id, user.id, insertedIds.Count, skippedNotes.Count)

            do! db.ClearBatch batch.id

            let summary =
                let addedPart =
                    if insertedIds.Count = 0 then "Ничего не добавлено."
                    else
                        let ids = insertedIds |> Seq.map string |> String.concat ", "
                        let word = RussianPlural.choose insertedIds.Count "купон" "купона" "купонов"
                        $"Добавлено {insertedIds.Count} {word}: ID:{ids}."
                let skipPart =
                    if skippedNotes.Count = 0 then ""
                    else
                        let notes = skippedNotes |> String.concat "; "
                        let word = RussianPlural.choose skippedNotes.Count "купон" "купона" "купонов"
                        $" Пропущено {skippedNotes.Count} {word}: {notes}."
                addedPart + skipPart

            do! this.EditBulkOrSend batch summary
        }

    member private this.HandleBulkCallback (user: DbUser) (chatId: int64) (data: string) =
        task {
            Metrics.callbackTotal.Add(1L, KeyValuePair("action", box "addflow_bulk"))
            Metrics.buttonClickTotal.Add(1L, KeyValuePair("button", box data))

            // parts: [| "addflow"; "bulk"; "confirm" | "cancel"; "<batchId>" |]
            let parts = data.Split(':', StringSplitOptions.RemoveEmptyEntries)
            if parts.Length < 4 then
                do! sendText chatId "Не понял действие."
            else
                match Int64.TryParse(parts[3]) with
                | false, _ ->
                    do! sendText chatId "Не понял идентификатор пакета."
                | true, batchId ->
                    // Tag the parent handleCallbackQuery span so the trace is
                    // searchable by batchId in Tempo (mirrors finalizeBatch's tag).
                    if not (isNull Activity.Current) then
                        %Activity.Current.SetTag("batchId", batchId)
                    match parts[2] with
                    | "cancel"
                    | "confirm" ->
                        // Atomic claim: only one of confirm/cancel wins the row;
                        // the loser sees None and answers "уже устарел". This is
                        // what prevents the confirm-and-cancel double-fire from
                        // both running TryAddCoupon + EditBulkOrSend and leaving
                        // the user with a "Ок, пакет отменён" message after the coupon
                        // was actually added.
                        let! claimed = db.TryClaimAwaitingBatch(batchId, user.id)
                        match claimed with
                        | None ->
                            do! sendText chatId "Этот пакет уже устарел, отправь альбом заново."
                        | Some batch ->
                            if parts[2] = "cancel" then do! this.BulkBatchCancel batch
                            else do! this.BulkBatchConfirm user batch
                    | _ ->
                        do! sendText chatId "Не понял действие."
        }

    member this.HandleCallbackQuery (cq: CallbackQuery) =
        task {
            use a = botActivity.StartActivity("handleCallbackQuery")
            %a.SetTag("callbackQueryId", cq.Id)
            if not (isNull a) then %a.SetTag("callbackData", Option.toObj cq.Data)
            let mutable caughtExn = ValueNone
            try
                match Tg.callbackMessage cq with
                | None -> ()
                | Some (chat, messageId) ->
                    let chatId = chat.Id
                    %a.SetTag("chatId", chatId)
                    %a.SetTag("fromId", cq.From.Id)
                    let! ok = ensureCommunityMember cq.From.Id chatId
                    if not ok then () else

                    let! user =
                        { id = cq.From.Id
                          username = Option.toObj cq.From.Username
                          first_name = cq.From.FirstName
                          last_name = Option.toObj cq.From.LastName
                          created_at = time.GetUtcNow().UtcDateTime
                          updated_at = time.GetUtcNow().UtcDateTime }
                        |> db.UpsertUser

                    let isPrivateChat = chat.Type = ChatType.Private
                    let data = cq.Data |> Option.defaultValue ""
                    let hasData = cq.Data.IsSome

                    if isPrivateChat && hasData && data.StartsWith("take:") then
                        Metrics.callbackTotal.Add(1L, KeyValuePair("action", box "take"))
                        Metrics.buttonClickTotal.Add(1L, KeyValuePair("button", box "take"))
                        let idStr = data.Substring("take:".Length)
                        match BotHelpers.parseInt idStr with
                        | Some couponId ->
                            do! commandHandler.HandleTake user chatId couponId
                        | None ->
                            ()
                    elif isPrivateChat && hasData && data.StartsWith("addflow:bulk:") then
                        do! this.HandleBulkCallback user chatId data
                    elif isPrivateChat && hasData && data.StartsWith("addflow:") then
                        Metrics.callbackTotal.Add(1L, KeyValuePair("action", box "addflow"))
                        Metrics.buttonClickTotal.Add(1L, KeyValuePair("button", box data))
                        match! db.GetPendingAddFlow user.id with
                        | None ->
                            do! sendText chatId "Этот шаг добавления уже устарел. Начни заново: /add"
                        | Some flow ->
                            match data with
                            | d when d.StartsWith("addflow:disc:") ->
                                // addflow:disc:<value>:<min_check>
                                let parts = d.Split(':', StringSplitOptions.RemoveEmptyEntries)
                                if parts.Length >= 4 then
                                    match BotHelpers.parseDecimalInvariant parts[2], BotHelpers.parseDecimalInvariant parts[3] with
                                    | Some v, Some mc ->
                                        if flow.expires_at.HasValue then
                                            let next =
                                                { flow with
                                                    stage = "awaiting_confirm"
                                                    value = Nullable(v)
                                                    min_check = Nullable(mc)
                                                    updated_at = time.GetUtcNow().UtcDateTime }
                                            do! db.UpsertPendingAddFlow next
                                            let vf = if flow.valid_from.HasValue then Some flow.valid_from.Value else None
                                            do! couponFlow.HandleAddWizardSendConfirm chatId v mc flow.expires_at.Value flow.barcode_text vf
                                        else
                                            let next =
                                                { flow with
                                                    stage = "awaiting_date_choice"
                                                    value = Nullable(v)
                                                    min_check = Nullable(mc)
                                                    updated_at = time.GetUtcNow().UtcDateTime }
                                            do! db.UpsertPendingAddFlow next
                                            do! couponFlow.HandleAddWizardAskDate chatId
                                    | _ ->
                                        do! sendText chatId "Не понял значения. Попробуй ещё раз: /add"
                                else
                                    do! sendText chatId "Не понял значения. Попробуй ещё раз: /add"
                            | "addflow:date:today" ->
                                if flow.value.HasValue && flow.min_check.HasValue then
                                    let expiresAt = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime)
                                    let next =
                                        { flow with
                                            stage = "awaiting_confirm"
                                            expires_at = Nullable(expiresAt)
                                            updated_at = time.GetUtcNow().UtcDateTime }
                                    do! db.UpsertPendingAddFlow next
                                    let vf = if flow.valid_from.HasValue then Some flow.valid_from.Value else None
                                    do! couponFlow.HandleAddWizardSendConfirm chatId flow.value.Value flow.min_check.Value expiresAt flow.barcode_text vf
                                else
                                    do! sendText chatId "Сначала выбери скидку. Начни заново: /add"
                            | "addflow:date:tomorrow" ->
                                if flow.value.HasValue && flow.min_check.HasValue then
                                    let expiresAt = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime.AddDays(1.0))
                                    let next =
                                        { flow with
                                            stage = "awaiting_confirm"
                                            expires_at = Nullable(expiresAt)
                                            updated_at = time.GetUtcNow().UtcDateTime }
                                    do! db.UpsertPendingAddFlow next
                                    let vf = if flow.valid_from.HasValue then Some flow.valid_from.Value else None
                                    do! couponFlow.HandleAddWizardSendConfirm chatId flow.value.Value flow.min_check.Value expiresAt flow.barcode_text vf
                                else
                                    do! sendText chatId "Сначала выбери скидку. Начни заново: /add"
                            | "addflow:ocr:yes" ->
                                // If OCR fully recognized and user confirms, add immediately (no extra confirm screen).
                                if
                                    flow.stage = "awaiting_ocr_confirm"
                                    && flow.photo_file_id <> null
                                    && flow.value.HasValue
                                    && flow.min_check.HasValue
                                    && flow.expires_at.HasValue
                                then
                                    let vf = if flow.valid_from.HasValue then Some flow.valid_from.Value else None
                                    match!
                                        db.TryAddCoupon(
                                            user.id,
                                            flow.photo_file_id,
                                            flow.value.Value,
                                            flow.min_check.Value,
                                            flow.expires_at.Value,
                                            flow.barcode_text,
                                            ?validFrom = vf
                                        )
                                    with
                                    | AddCouponResult.Added coupon ->
                                        do! db.ClearPendingAddFlow user.id
                                        let v = coupon.value.ToString("0.##")
                                        let mc = coupon.min_check.ToString("0.##")
                                        let d = BotHelpers.formatUiDate coupon.expires_at
                                        do! sendText chatId $"Добавлен купон ID:{coupon.id}: {v}€ из {mc}€, до {d}"
                                    | AddCouponResult.Expired ->
                                        do! db.ClearPendingAddFlow user.id
                                        do! sendText chatId "Нельзя добавить истёкший купон (дата в прошлом). Начни заново: /add"
                                    | AddCouponResult.DuplicatePhoto existingId ->
                                        do! db.ClearPendingAddFlow user.id
                                        do! sendText chatId $"Похоже, этот купон уже был добавлен ранее (та же фотография). Уже есть купон ID:{existingId}. Начни заново: /add"
                                    | AddCouponResult.DuplicateBarcode existingId ->
                                        do! db.ClearPendingAddFlow user.id
                                        do! sendText chatId $"Купон с таким штрихкодом уже есть в базе и ещё не истёк. Уже есть купон ID:{existingId}. Начни заново: /add"
                                else
                                    do! sendText chatId "Этот шаг уже неактуален. Начни заново: /add"
                            | "addflow:ocr:no" ->
                                // Clear OCR suggestion and continue manually; keep barcode (already validated at photo upload).
                                let next =
                                    { flow with
                                        stage = "awaiting_discount_choice"
                                        value = Nullable()
                                        min_check = Nullable()
                                        expires_at = Nullable()
                                        updated_at = time.GetUtcNow().UtcDateTime }
                                do! db.UpsertPendingAddFlow next
                                do!
                                    BotHelpers.sendTextMarkup tg chatId
                                        "Ок, выбери скидку и минимальный чек.\nИли просто напиши следующим сообщением: \"10 50\" или \"10/50\"."
                                        (BotHelpers.addWizardDiscountKeyboard())
                            | "addflow:confirm" ->
                                if flow.photo_file_id <> null && flow.value.HasValue && flow.min_check.HasValue && flow.expires_at.HasValue then
                                    let vf = if flow.valid_from.HasValue then Some flow.valid_from.Value else None
                                    match!
                                        db.TryAddCoupon(
                                            user.id,
                                            flow.photo_file_id,
                                            flow.value.Value,
                                            flow.min_check.Value,
                                            flow.expires_at.Value,
                                            flow.barcode_text,
                                            ?validFrom = vf
                                        )
                                    with
                                    | AddCouponResult.Added coupon ->
                                        do! db.ClearPendingAddFlow user.id
                                        let v = coupon.value.ToString("0.##")
                                        let mc = coupon.min_check.ToString("0.##")
                                        let d = BotHelpers.formatUiDate coupon.expires_at
                                        do! sendText chatId $"Добавлен купон ID:{coupon.id}: {v}€ из {mc}€, до {d}"
                                    | AddCouponResult.Expired ->
                                        do! db.ClearPendingAddFlow user.id
                                        do! sendText chatId "Нельзя добавить истёкший купон (дата в прошлом). Начни заново: /add"
                                    | AddCouponResult.DuplicatePhoto existingId ->
                                        do! db.ClearPendingAddFlow user.id
                                        do! sendText chatId $"Похоже, этот купон уже был добавлен ранее (та же фотография). Уже есть купон ID:{existingId}. Начни заново: /add"
                                    | AddCouponResult.DuplicateBarcode existingId ->
                                        do! db.ClearPendingAddFlow user.id
                                        do! sendText chatId $"Купон с таким штрихкодом уже есть в базе и ещё не истёк. Уже есть купон ID:{existingId}. Начни заново: /add"
                                else
                                    do! sendText chatId "Не хватает данных для добавления. Начни заново: /add"
                            | "addflow:cancel" ->
                                do! db.ClearPendingAddFlow user.id
                                do! sendText chatId "Ок, добавление купона отменено."
                            | _ ->
                                do! sendText chatId "Не понял действие. Начни заново: /add"
                    elif isPrivateChat && hasData && data.StartsWith("return:") then
                        Metrics.callbackTotal.Add(1L, KeyValuePair("action", box "return"))
                        let deleteOnSuccess = data.EndsWith(":del")
                        let baseData = if deleteOnSuccess then data.Substring(0, data.Length - 4) else data
                        let idStr = baseData.Substring("return:".Length)
                        match BotHelpers.parseInt idStr with
                        | Some couponId ->
                            let! ok = commandHandler.HandleReturn user chatId couponId
                            if ok && deleteOnSuccess then
                                try
                                    do! BotHelpers.deleteMessage tg chatId messageId
                                with _ -> ()
                        | None -> ()
                    elif isPrivateChat && hasData && data.StartsWith("used:") then
                        Metrics.callbackTotal.Add(1L, KeyValuePair("action", box "used"))
                        let deleteOnSuccess = data.EndsWith(":del")
                        let baseData = if deleteOnSuccess then data.Substring(0, data.Length - 4) else data
                        let idStr = baseData.Substring("used:".Length)
                        match BotHelpers.parseInt idStr with
                        | Some couponId ->
                            let! ok = commandHandler.HandleUsed user chatId couponId
                            if ok && deleteOnSuccess then
                                try
                                    do! BotHelpers.deleteMessage tg chatId messageId
                                with _ -> ()
                        | None -> ()
                    elif isPrivateChat && hasData && data.StartsWith("void:") then
                        Metrics.callbackTotal.Add(1L, KeyValuePair("action", box "void"))
                        let deleteOnSuccess = data.EndsWith(":del")
                        let baseData = if deleteOnSuccess then data.Substring(0, data.Length - 4) else data
                        let idStr = baseData.Substring("void:".Length)
                        match BotHelpers.parseInt idStr with
                        | Some couponId ->
                            let isAdmin = options.Value.FeedbackAdminIds |> Array.contains user.id
                            do! commandHandler.HandleVoid user chatId couponId isAdmin deleteOnSuccess (Some messageId)
                        | None -> ()
                    elif isPrivateChat && hasData && data = "myAdded" then
                        Metrics.callbackTotal.Add(1L, KeyValuePair("action", box "myAdded"))
                        do! commandHandler.HandleAdded user chatId
            with ex ->
                caughtExn <- ValueSome (ExceptionDispatchInfo.Capture(ex))
            try
                do! tg.CallExn(Funogram.Telegram.Req.AnswerCallbackQuery.Make(cq.Id)) |> taskIgnore
            with _ -> ()
            match caughtExn with
            | ValueSome edi -> edi.Throw()
            | ValueNone -> ()
        }
