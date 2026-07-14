namespace CouponHubBot.Services

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Telegram.Bot
open Telegram.Bot.Types
open Telegram.Bot.Types.ReplyMarkups
open CouponHubBot
open CouponHubBot.Services
open CouponHubBot.Telemetry
open CouponHubBot.Utils
open BotInfra

type CouponFlowHandler(
    botClient: ITelegramBotClient,
    botOptions: IOptions<BotConfiguration>,
    ocrOptions: IOptions<BotOcrConfig>,
    db: DbService,
    couponOcr: CouponOcrEngine,
    batchDebounce: BatchDebounce,
    time: TimeProvider,
    logger: ILogger<CouponFlowHandler>
) =
    let sendText = BotHelpers.sendText botClient

    // Best-effort wrappers for cosmetic Telegram calls whose failure should
    // never fail the surrounding flow (e.g. deleting an already-gone placeholder).
    let tryDeleteMessage (chatId: int64) (messageId: int64) : Task =
        task {
            try do! botClient.DeleteMessage(ChatId chatId, int messageId)
            with _ -> ()
        } :> Task

    let tryEditMessage (chatId: int64) (messageId: int64) (text: string) : Task<bool> =
        task {
            try
                do! botClient.EditMessageText(ChatId chatId, int messageId, text) |> taskIgnore
                return true
            with _ -> return false
        }

    // In-memory nudge throttles. Lost on restart — user may see one extra nudge,
    // which is fine. Keyed by user id; the date is the last UTC day on which we nudged.
    let nudgedAddCmdToday    = ConcurrentDictionary<int64, DateOnly>()
    let nudgedAlbumToday     = ConcurrentDictionary<int64, DateOnly>()
    let lastSinglePhotoAt    = ConcurrentDictionary<int64, DateTimeOffset>()

    let addCmdNudgeLine =
        "\n\n_(подсказка: можно просто прислать фото без команды /add)_"
    let albumNudgeLine =
        "\n\n_(подсказка: можно выделить несколько фото сразу и отправить альбомом)_"

    let buildConfirmTextAndKeyboard (value: decimal) (minCheck: decimal) (expiresAt: DateOnly) (barcodeText: string | null) (validFrom: DateOnly option) =
        let v = value.ToString("0.##")
        let mc = minCheck.ToString("0.##")
        let d = BotHelpers.formatUiDate expiresAt
        let barcodeStr =
            if String.IsNullOrWhiteSpace barcodeText then ""
            else $", штрихкод: {barcodeText}"
        let validFromStr =
            match validFrom with
            | Some vf -> $", с {BotHelpers.formatUiDate vf}"
            | None -> ""
        let kb = BotHelpers.addWizardConfirmKeyboard ()
        let text = $"Подтвердить добавление купона: {v}€ из {mc}€{validFromStr}, до {d}{barcodeStr}?"
        text, kb

    member _.HandleAddWizardStart (user: DbUser) (chatId: int64) =
        task {
            do! db.UpsertPendingAddFlow(
                    { user_id = user.id
                      stage = "awaiting_photo"
                      photo_file_id = null
                      value = Nullable()
                      min_check = Nullable()
                      expires_at = Nullable()
                      barcode_text = null
                      valid_from = Nullable()
                      updated_at = time.GetUtcNow().UtcDateTime }
                )
            let today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime)
            let shouldNudge =
                match nudgedAddCmdToday.TryGetValue user.id with
                | true, d when d = today -> false
                | _ -> true
            let text =
                if shouldNudge then
                    nudgedAddCmdToday[user.id] <- today
                    "Пришли фото купона (просто картинку)." + addCmdNudgeLine
                else
                    "Пришли фото купона (просто картинку)."
            do! sendText chatId text
        }

    member _.HandleAddManual (user: DbUser) (msg: Message) =
        task {
            use a = botActivity.StartActivity("handleAdd")
            %a.SetTag("userId", user.id)

            let chatId = msg.Chat.Id
            let caption = msg.Caption
            let hasPhoto = not (isNull msg.Photo) && msg.Photo.Length > 0
            let parts =
                if isNull caption then [||]
                else caption.Split([|' '|], System.StringSplitOptions.RemoveEmptyEntries)

            if not hasPhoto then
                do! sendText chatId "Для ручного добавления пришли фото купона с подписью:\n/add <discount> <min_check> <date>\nНапример: /add 10 50 25.01.2026 (или просто день: /add 10 50 25)"
            elif parts.Length >= 4 && (parts[0] = "/add" || parts[0] = "/a") then
                let valueOpt =
                    BotHelpers.parseDecimalInvariant parts[1]
                let minCheckOpt =
                    BotHelpers.parseDecimalInvariant parts[2]
                let dateOpt = BotHelpers.tryParseDateOnly time parts[3]
                match valueOpt, minCheckOpt, dateOpt with
                | Some value, Some minCheck, Some expiresAt ->
                    let largestPhoto =
                        msg.Photo
                        |> Array.maxBy (fun p -> if p.FileSize.HasValue then p.FileSize.Value else 0)
                    match! db.TryAddCoupon(user.id, largestPhoto.FileId, value, minCheck, expiresAt, null) with
                    | AddCouponResult.Added coupon ->
                        let v = coupon.value.ToString("0.##")
                        let mc = coupon.min_check.ToString("0.##")
                        let d = BotHelpers.formatUiDate coupon.expires_at
                        do! sendText chatId $"Добавлен купон ID:{coupon.id}: {v}€ из {mc}€, до {d}"
                    | AddCouponResult.Expired ->
                        do! sendText chatId "Нельзя добавить истёкший купон (дата в прошлом). Проверь дату и попробуй ещё раз."
                    | AddCouponResult.DuplicatePhoto existingId ->
                        do! sendText chatId $"Похоже, этот купон уже был добавлен ранее (та же фотография). Уже есть купон ID:{existingId}."
                    | AddCouponResult.DuplicateBarcode existingId ->
                        do! sendText chatId $"Купон с таким штрихкодом уже есть в базе и ещё не истёк. Уже есть купон ID:{existingId}."
                | _ ->
                    do! sendText chatId "Не понял discount/min_check/date. Примеры: /add 10 50 2026-01-25 (или /add 10 50 25.01.2026, или /add 10 50 25)"
            else
                do! sendText chatId "Нужна подпись вида: /add <discount> <min_check> <date>\nНапример: /add 10 50 25.01.2026"
        }

    member _.HandleAddWizardPhoto (user: DbUser) (chatId: int64) (photoFileId: string) =
        task {
            // Rapid-singles nudge: detect ≥2 single photos within 5s, hint album upload once per day.
            let nowOffset = time.GetUtcNow()
            let isRapid =
                match lastSinglePhotoAt.TryGetValue user.id with
                | true, prev -> (nowOffset - prev).TotalSeconds < 5.0
                | _ -> false
            lastSinglePhotoAt[user.id] <- nowOffset
            let nudgeToday = DateOnly.FromDateTime(nowOffset.UtcDateTime)
            let shouldAlbumNudge =
                isRapid
                && (match nudgedAlbumToday.TryGetValue user.id with
                    | true, d when d = nudgeToday -> false
                    | _ -> true)
            if shouldAlbumNudge then nudgedAlbumToday[user.id] <- nudgeToday

            if shouldAlbumNudge then
                do! sendText chatId (albumNudgeLine.TrimStart())

            do! db.UpsertPendingAddFlow(
                    { user_id = user.id
                      stage = "awaiting_discount_choice"
                      photo_file_id = photoFileId
                      value = Nullable()
                      min_check = Nullable()
                      expires_at = Nullable()
                      barcode_text = null
                      valid_from = Nullable()
                      updated_at = time.GetUtcNow().UtcDateTime }
                )

            let ocrConfig = ocrOptions.Value
            if not ocrConfig.OcrEnabled then
                logger.LogInformation("OCR disabled; falling back to manual entry for user {UserId}", user.id)
                do!
                    botClient.SendMessage(
                        ChatId chatId,
                        "Выбери скидку и минимальный чек.\nИли просто напиши следующим сообщением: \"10 50\" или \"10/50\".",
                        replyMarkup = BotHelpers.addWizardDiscountKeyboard()
                    )
                    |> taskIgnore
            else
                // Attempt OCR prefill (optional). Download photo into memory, then run OCR engine.
                let! file = botClient.GetFile(photoFileId)
                if String.IsNullOrWhiteSpace(file.FilePath) then
                    logger.LogWarning("Telegram file.FilePath missing for {FileId}; falling back to manual entry", photoFileId)
                    do!
                        botClient.SendMessage(
                            ChatId chatId,
                            "Выбери скидку и минимальный чек.\nИли просто напиши следующим сообщением: \"10 50\" или \"10/50\".",
                            replyMarkup = BotHelpers.addWizardDiscountKeyboard()
                        )
                        |> taskIgnore
                else
                    use ms = new System.IO.MemoryStream()
                    do! botClient.DownloadFile(file.FilePath, ms)
                    let bytes = ms.ToArray()

                    if int64 bytes.Length > ocrConfig.OcrMaxFileSizeBytes then
                        do!
                            botClient.SendMessage(
                                ChatId chatId,
                                "Картинка слишком большая для распознавания. Выбери скидку и минимальный чек.\nИли просто напиши следующим сообщением: \"10 50\" или \"10/50\".",
                                replyMarkup = BotHelpers.addWizardDiscountKeyboard()
                            )
                            |> taskIgnore
                    else
                        // OCR runs synchronously inside the webhook update handler, but
                        // Recognize degrades a transient/timeout failure (retried by the
                        // Azure OCR SDK) to a null-field result rather than
                        // throwing — so a slow Azure OCR can't crash the handler. The
                        // barcode from ZXing may still be present, in which case the wizard
                        // falls through to manual discount/min-check entry below.
                        let! ocr = couponOcr.Recognize(ReadOnlyMemory<byte>(bytes))

                        let valueOpt =
                            if ocr.couponValue.HasValue then
                                Some ocr.couponValue.Value
                            else None
                        let minCheckOpt =
                            if ocr.minCheck.HasValue then
                                Some ocr.minCheck.Value
                            else None
                        let validToOpt =
                            if ocr.validTo.HasValue then
                                Some (DateOnly.FromDateTime(ocr.validTo.Value))
                            else None
                        let validFromOpt =
                            if ocr.validFrom.HasValue then
                                Some (DateOnly.FromDateTime(ocr.validFrom.Value))
                            else None
                        let validFromNullable =
                            match validFromOpt with
                            | Some vf -> Nullable(vf)
                            | None -> Nullable()
                        let barcodeText =
                            if String.IsNullOrWhiteSpace ocr.barcode then null else ocr.barcode

                        logger.LogInformation(
                            "Single-photo OCR result for user {UserId}: hasBarcode={HasBarcode} hasValue={HasValue} hasMinCheck={HasMinCheck} hasValidTo={HasValidTo}",
                            user.id, not (isNull barcodeText), valueOpt.IsSome, minCheckOpt.IsSome, validToOpt.IsSome)

                        if isNull barcodeText then
                            // Barcode not recognized — photo quality is insufficient.
                            do! db.UpsertPendingAddFlow(
                                    { user_id = user.id
                                      stage = "awaiting_photo"
                                      photo_file_id = null
                                      value = Nullable()
                                      min_check = Nullable()
                                      expires_at = Nullable()
                                      barcode_text = null
                                      valid_from = Nullable()
                                      updated_at = time.GetUtcNow().UtcDateTime }
                                )
                            do! sendText chatId "Не удалось распознать штрихкод на фото. Пожалуйста, пришли фото в лучшем качестве или скадрируй картинку ближе к штрихкоду, дате и сумме."
                        else

                        // Persist whatever we managed to recognize, and continue the wizard from the first missing step.
                        match valueOpt, minCheckOpt, validToOpt with
                        | Some value, Some minCheck, Some expiresAt ->
                            do! db.UpsertPendingAddFlow(
                                    { user_id = user.id
                                      stage = "awaiting_ocr_confirm"
                                      photo_file_id = photoFileId
                                      value = Nullable(value)
                                      min_check = Nullable(minCheck)
                                      expires_at = Nullable(expiresAt)
                                      barcode_text = barcodeText
                                      valid_from = validFromNullable
                                      updated_at = time.GetUtcNow().UtcDateTime }
                                )
                            let v = value.ToString("0.##")
                            let mc = minCheck.ToString("0.##")
                            let d = BotHelpers.formatUiDate expiresAt
                            do!
                                botClient.SendMessage(
                                    ChatId chatId,
                                    $"Я распознал: {v}€ из {mc}€, до {d}, штрихкод: {barcodeText}. Всё верно?",
                                    replyMarkup = BotHelpers.addWizardOcrKeyboard()
                                )
                                |> taskIgnore
                        | Some value, Some minCheck, None ->
                            do! db.UpsertPendingAddFlow(
                                    { user_id = user.id
                                      stage = "awaiting_date_choice"
                                      photo_file_id = photoFileId
                                      value = Nullable(value)
                                      min_check = Nullable(minCheck)
                                      expires_at = Nullable()
                                      barcode_text = barcodeText
                                      valid_from = validFromNullable
                                      updated_at = time.GetUtcNow().UtcDateTime }
                                )
                            let v = value.ToString("0.##")
                            let mc = minCheck.ToString("0.##")
                            do!
                                botClient.SendMessage(
                                    ChatId chatId,
                                    $"Я распознал скидку: {v}€ из {mc}€. Теперь выбери дату истечения (или напиши \"25\", \"25.01.2026\", \"2026-01-25\"):",
                                    replyMarkup = BotHelpers.addWizardDateKeyboard()
                                )
                                |> taskIgnore
                        | _ ->
                            do! db.UpsertPendingAddFlow(
                                    { user_id = user.id
                                      stage = "awaiting_discount_choice"
                                      photo_file_id = photoFileId
                                      value = Nullable()
                                      min_check = Nullable()
                                      expires_at =
                                        match validToOpt with
                                        | Some d -> Nullable(d)
                                        | None -> Nullable()
                                      barcode_text = barcodeText
                                      valid_from = validFromNullable
                                      updated_at = time.GetUtcNow().UtcDateTime }
                                )
                            let text =
                                match validToOpt with
                                | Some expiresAt ->
                                    let d = BotHelpers.formatUiDate expiresAt
                                    $"Я распознал дату истечения {d}. Теперь выбери скидку и минимальный чек.\nИли просто напиши следующим сообщением: \"10 50\" или \"10/50\"."
                                | None -> "Выбери скидку и минимальный чек.\nИли просто напиши следующим сообщением: \"10 50\" или \"10/50\"."
                            do!
                                botClient.SendMessage(
                                    ChatId chatId,
                                    text,
                                    replyMarkup = BotHelpers.addWizardDiscountKeyboard()
                                )
                                |> taskIgnore
        }

    member _.HandleAddWizardAskDate (chatId: int64) =
        botClient.SendMessage(ChatId chatId, "Выбери дату истечения (или напиши \"25\", \"25.01.2026\", \"2026-01-25\"):", replyMarkup = BotHelpers.addWizardDateKeyboard())
        |> taskIgnore

    member _.HandleAddWizardSendConfirm (chatId: int64) (value: decimal) (minCheck: decimal) (expiresAt: DateOnly) (barcodeText: string | null) (validFrom: DateOnly option) =
        let text, kb = buildConfirmTextAndKeyboard value minCheck expiresAt barcodeText validFrom
        botClient.SendMessage(ChatId chatId, text, replyMarkup = kb) |> taskIgnore

    member _.HandleAddWizardEditConfirm (chatId: int64) (messageId: int) (value: decimal) (minCheck: decimal) (expiresAt: DateOnly) (barcodeText: string | null) (validFrom: DateOnly option) =
        let text, kb = buildConfirmTextAndKeyboard value minCheck expiresAt barcodeText validFrom
        task {
            try
                do! botClient.EditMessageText(ChatId chatId, messageId, text, replyMarkup = kb) |> taskIgnore
            with _ ->
                do! botClient.SendMessage(ChatId chatId, text, replyMarkup = kb) |> taskIgnore
        }

    /// Tries to advance the add wizard for a non-command message.
    /// Returns true if the message was consumed by the wizard, false otherwise.
    member this.TryHandleWizardMessage (user: DbUser) (msg: Message) =
        task {
            let! pendingFlow = db.GetPendingAddFlow(user.id)
            match pendingFlow with
            | None ->
                // UX: if no pending flow is active, a plain photo starts /add implicitly.
                // Do NOT override explicit /add manual flow via caption.
                match BotHelpers.getLargestPhotoFileId msg with
                | Some photoFileId when isNull msg.Caption || (not (msg.Caption.StartsWith("/add")) && not (msg.Caption.StartsWith("/a"))) ->
                    do! this.HandleAddWizardPhoto user msg.Chat.Id photoFileId
                    return true
                | _ -> return false
            | Some _ when pendingFlow.Value.stage = "awaiting_photo" ->
                match BotHelpers.getLargestPhotoFileId msg with
                | Some photoFileId ->
                    do! this.HandleAddWizardPhoto user msg.Chat.Id photoFileId
                    return true
                | None -> return false
            | Some flow when flow.stage = "awaiting_discount_choice" && not (isNull msg.Text) ->
                match BotHelpers.tryParseTwoDecimals msg.Text with
                | Some (v, mc) ->
                    if flow.expires_at.HasValue then
                        do! db.UpsertPendingAddFlow(
                                { flow with
                                    stage = "awaiting_confirm"
                                    value = Nullable(v)
                                    min_check = Nullable(mc)
                                    updated_at = time.GetUtcNow().UtcDateTime }
                            )
                        let vf = if flow.valid_from.HasValue then Some flow.valid_from.Value else None
                        do! this.HandleAddWizardSendConfirm msg.Chat.Id v mc flow.expires_at.Value flow.barcode_text vf
                    else
                        do! db.UpsertPendingAddFlow(
                                { flow with
                                    stage = "awaiting_date_choice"
                                    value = Nullable(v)
                                    min_check = Nullable(mc)
                                    updated_at = time.GetUtcNow().UtcDateTime }
                            )
                        do! this.HandleAddWizardAskDate msg.Chat.Id
                    return true
                | None ->
                    do! sendText msg.Chat.Id "Не понял. Пришли два числа: скидка и минимальный чек. Например: 10 50 или 10/50"
                    return true
            | Some flow when flow.stage = "awaiting_date_choice" && not (isNull msg.Text) ->
                match BotHelpers.tryParseDateOnly time msg.Text with
                | Some expiresAt ->
                    if flow.value.HasValue && flow.min_check.HasValue then
                        let todayUtc = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime)
                        if expiresAt < todayUtc then
                            // Don't allow past dates (today is ok).
                            do! sendText msg.Chat.Id "Эта дата уже в прошлом. Нельзя добавить истёкший купон. Пришли дату заново."
                        else
                            do! db.UpsertPendingAddFlow({ flow with stage = "awaiting_confirm"; expires_at = Nullable(expiresAt); updated_at = time.GetUtcNow().UtcDateTime })
                            let vf = if flow.valid_from.HasValue then Some flow.valid_from.Value else None
                            do! this.HandleAddWizardSendConfirm msg.Chat.Id flow.value.Value flow.min_check.Value expiresAt flow.barcode_text vf
                    else
                        do! sendText msg.Chat.Id "Сначала выбери скидку. Начни заново: /add"
                    return true
                | None ->
                    do! sendText msg.Chat.Id "Не понял дату. Примеры: 25, 25.01.2026 или 2026-01-25"
                    return true
            | Some _ ->
                // If user sends a photo at a stage where we don't expect photos (e.g. awaiting_confirm),
                // don't silently ignore: warn how to proceed.
                if msg.Photo <> null && msg.Photo.Length > 0 then
                    do! sendText msg.Chat.Id "Сейчас идёт добавление купона. Закончи текущий шаг или начни заново: /add"
                    return true
                else
                    return false
        }

    // ── Album batch flow ────────────────────────────────────────────────

    /// Format one OK item for the bulk-confirm message.
    /// Mirrors the single-photo confirm text style.
    member private _.FormatBatchItemLine (item: PendingAddBatchItem) : string =
        let v = item.value.Value.ToString("0.##")
        let mc = item.min_check.Value.ToString("0.##")
        let d = BotHelpers.formatUiDate item.expires_at.Value
        let bc = item.barcode_text
        let bcSuffix =
            if String.IsNullOrWhiteSpace bc then ""
            else
                let tail =
                    if bc.Length >= 4 then bc.Substring(bc.Length - 4) else bc
                $" ···{tail}"
        $"• {v}€ из {mc}€, до {d}{bcSuffix}"

    /// Builds the bulk-confirm message body + keyboard from the final batch snapshot.
    member this.RenderBulkConfirm (batchId: int64) (items: PendingAddBatchItem array) : string * InlineKeyboardMarkup =
        let okItems = items |> Array.filter (fun i -> i.status = "ok")
        if okItems.Length = 0 then
            "Не смог распознать ни одного купона.", BotHelpers.addBatchConfirmKeyboard batchId 0
        else
            let couponWord = RussianPlural.choose okItems.Length "купон" "купона" "купонов"
            let header = $"Подтвердить {okItems.Length} {couponWord}:"
            let lines = okItems |> Array.map this.FormatBatchItemLine
            let body = String.concat "\n" lines
            $"{header}\n{body}", BotHelpers.addBatchConfirmKeyboard batchId okItems.Length

    /// Background OCR for one item. Fire-and-forget from the webhook handler.
    /// Per-attempt timeout and one retry on transient failures are handled by
    /// the Azure OCR SDK's built-in pipeline. All writes are
    /// conditional on status='pending' so a late OCR finish after finalize's
    /// claim is a safe no-op.
    member _.OcrItem (batchId: int64) (itemId: int64) (photoFileId: string) (parent: ActivityContext option) : Task =
        task {
            use a =
                match parent with
                | Some ctx -> botActivity.StartActivity("batchOcrItem", ActivityKind.Internal, ctx)
                | None -> botActivity.StartActivity("batchOcrItem")
            if not (isNull a) then
                %a.SetTag("batchId", batchId)
                %a.SetTag("itemId", itemId)

            let recordOk () =
                if not (isNull a) then %a.SetTag("outcome", "ok")
                Metrics.batchItemOutcomeTotal.Add(1L, KeyValuePair("outcome", box "ok"))
                logger.LogInformation(
                    "Batch {BatchId} item {ItemId}: outcome=ok",
                    batchId, itemId)
            let writeNeedsInput note =
                task {
                    do! db.UpdateBatchItemNeedsInput(itemId, note)
                    if not (isNull a) then
                        %a.SetTag("outcome", "needs_input")
                        %a.SetTag("failure_note", note)
                    Metrics.batchItemOutcomeTotal.Add(
                        1L,
                        KeyValuePair("outcome", box "needs_input"),
                        KeyValuePair("failure_note", box note))
                    logger.LogInformation(
                        "Batch {BatchId} item {ItemId}: outcome=needs_input note={FailureNote}",
                        batchId, itemId, note)
                }

            try
                let ocrConfig = ocrOptions.Value

                if not ocrConfig.OcrEnabled then
                    do! writeNeedsInput "OCR disabled"
                else
                    let! file = botClient.GetFile(photoFileId)
                    if isNull file || String.IsNullOrWhiteSpace file.FilePath then
                        do! writeNeedsInput "OCR failed"
                    else
                        use ms = new System.IO.MemoryStream()
                        do! botClient.DownloadFile(file.FilePath, ms)
                        let bytes = ms.ToArray()
                        if int64 bytes.Length > ocrConfig.OcrMaxFileSizeBytes then
                            do! writeNeedsInput "OCR failed"
                        else
                            // Transient/timeout failures are retried by the OCR HTTP
                            // resilience pipeline; Recognize then degrades to a null-field
                            // result, which falls through to needs_input below.
                            let! ocr = couponOcr.Recognize(ReadOnlyMemory<byte>(bytes))

                            // Barcode comes from ZXing on the raw image bytes — independent of
                            // Azure OCR. If Azure fails (e.g. 403, VNet block) ZXing can still
                            // decode the barcode while value/min/date come back NULL. Treating
                            // that as 'ok' crashes RenderBulkConfirm later. Require ALL fields.
                            let hasAllFields =
                                not ocr.backendFailed
                                && not (String.IsNullOrWhiteSpace ocr.barcode)
                                && ocr.couponValue.HasValue
                                && ocr.minCheck.HasValue
                                && ocr.validTo.HasValue
                            if not hasAllFields then
                                let note =
                                    // A backend failure (Azure down/timed out, already retried) is distinct
                                    // from a readable photo missing a barcode — report it as such.
                                    if ocr.backendFailed then "OCR failed"
                                    elif String.IsNullOrWhiteSpace ocr.barcode then "no barcode"
                                    else "partial"
                                do! writeNeedsInput note
                            else
                                let validFromNullable =
                                    if ocr.validFrom.HasValue then
                                        Nullable(DateOnly.FromDateTime(ocr.validFrom.Value))
                                    else Nullable()
                                let expiresNullable = Nullable(DateOnly.FromDateTime(ocr.validTo.Value))
                                let! _ =
                                    db.UpdateBatchItemOcrOk(
                                        itemId,
                                        ocr.couponValue,
                                        ocr.minCheck,
                                        expiresNullable,
                                        ocr.barcode,
                                        validFromNullable)
                                recordOk ()
            with ex ->
                logger.LogWarning(ex, "OCR failed for batch {BatchId} item {ItemId}", batchId, itemId)
                try do! writeNeedsInput "OCR failed"
                with ex2 ->
                    logger.LogError(ex2, "Also failed to write OCR-failed status for item {ItemId}", itemId)
        } :> Task

    /// Per-failed-photo reply text, varied by failure reason.
    member private _.NeedsInputReplyText (failureNote: string | null) : string =
        match failureNote with
        | null -> "Не смог распознать этот купон. Пришли его отдельно."
        | s when s = "timeout" -> "Этот купон не успел обработаться — пришли его отдельно."
        | s when s = "OCR failed" -> "Не получилось распознать этот купон. Пришли его отдельно."
        | s when s = "partial" -> "Распознал штрихкод, но не разобрал сумму/срок. Пришли этот купон отдельно через /add."
        | _ -> "Не смог распознать этот купон. Пришли его отдельно."

    /// Sends one reply per needs_input item, targeting the original photo.
    /// Falls back to a plain message if Telegram rejects the reply (the user
    /// may have deleted the photo we're trying to reply to).
    member private this.SendPerPhotoReplies (batch: PendingAddBatch) (items: PendingAddBatchItem array) : Task =
        task {
            let needsInput = items |> Array.filter (fun i -> i.status = "needs_input")
            for item in needsInput do
                let replyText = this.NeedsInputReplyText item.failure_note
                let replyParams =
                    ReplyParameters(
                        MessageId = int item.photo_message_id,
                        AllowSendingWithoutReply = true)
                try
                    do! botClient.SendMessage(
                            ChatId batch.bulk_chat_id,
                            replyText,
                            replyParameters = replyParams)
                        |> taskIgnore
                with _ ->
                    try do! sendText batch.bulk_chat_id replyText with _ -> ()
        } :> Task

    /// Happy path: render the bulk-confirm, replace the placeholder with a
    /// fresh notifying message, link it back to the batch, and emit per-photo
    /// replies for items needing user input.
    member private this.RenderAndSendBulkConfirm (batchId: int64) (batch: PendingAddBatch) (items: PendingAddBatchItem array) : Task =
        task {
            let text, kb = this.RenderBulkConfirm batchId items

            // Replace the placeholder with a fresh send so the user gets a notification.
            if batch.bulk_message_id.HasValue then
                do! tryDeleteMessage batch.bulk_chat_id batch.bulk_message_id.Value

            let! sent = botClient.SendMessage(ChatId batch.bulk_chat_id, text, replyMarkup = kb)
            let! linked = db.SetBatchBulkMessageId(batchId, int64 sent.MessageId)
            if not linked then
                // Batch was abandoned between SendMessage and SetBatchBulkMessageId.
                // The bulk-confirm message has no batch to act on; delete the orphan.
                do! tryDeleteMessage batch.bulk_chat_id (int64 sent.MessageId)

            do! this.SendPerPhotoReplies batch items
        } :> Task

    /// Last-resort path when the render/send pipeline throws. Edits the
    /// placeholder in-place (or sends a fresh fallback) and always clears the
    /// batch so the user can immediately retry with the same media_group_id.
    member private _.SendFinalizeFallback (batchId: int64) : Task =
        task {
            let fallbackText = "Что-то пошло не так при обработке альбома. Попробуй прислать его ещё раз."
            try
                let! batchOpt = db.GetBatchById batchId
                match batchOpt with
                | None -> ()
                | Some batch ->
                    let! edited =
                        if batch.bulk_message_id.HasValue then
                            tryEditMessage batch.bulk_chat_id batch.bulk_message_id.Value fallbackText
                        else
                            Task.FromResult false
                    if not edited then
                        try do! sendText batch.bulk_chat_id fallbackText
                        with ex -> logger.LogError(ex, "Fallback sendMessage also failed for batch {BatchId}", batchId)
            with ex ->
                logger.LogError(ex, "Fallback handler itself failed for batch {BatchId}", batchId)

            try do! db.ClearBatch batchId
            with ex -> logger.LogError(ex, "Failed to clear failed batch {BatchId}", batchId)
        } :> Task

    /// Close-handler invoked by BatchDebounce after debounceMs of silence.
    /// Atomically flips status, claims any still-pending items as timeout,
    /// then delegates the user-facing work to RenderAndSendBulkConfirm. Any
    /// unexpected exception in that pipeline is caught and surfaced to the
    /// user via SendFinalizeFallback — a single bug or upstream outage must
    /// never leave the user stuck on the "обрабатываю купоны…" placeholder.
    /// Safe to call concurrently — TryFlipBatchToAwaiting enforces single-winner.
    member this.FinalizeBatch (batchId: int64) : Task =
        task {
            use a = botActivity.StartActivity("finalizeBatch")
            if not (isNull a) then %a.SetTag("batchId", batchId)

            let! won = db.TryFlipBatchToAwaiting batchId
            if not won then () else

            let! claimedCount = db.ClaimPendingItemsAsTimeout batchId
            if claimedCount > 0 then
                logger.LogInformation("Batch {BatchId}: {Count} item(s) timed out at finalize", batchId, claimedCount)

            let! batchOpt = db.GetBatchById batchId
            match batchOpt with
            | None -> ()
            | Some batch ->
                let! items = db.GetBatchItems batchId
                let itemCount = items.Length
                if not (isNull a) then %a.SetTag("itemCount", itemCount)

                let mutable outcome = "ok"
                try
                    do! this.RenderAndSendBulkConfirm batchId batch items
                with ex ->
                    outcome <- "fallback"
                    logger.LogError(ex, "FinalizeBatch crashed for batch {BatchId}; sending fallback", batchId)
                    do! this.SendFinalizeFallback batchId

                if not (isNull a) then %a.SetTag("outcome", outcome)
                Metrics.batchFinalizedTotal.Add(1L, KeyValuePair("outcome", box outcome))
                Metrics.batchSize.Record(itemCount, KeyValuePair("outcome", box outcome))
                logger.LogInformation(
                    "Batch {BatchId} finalized: outcome={Outcome} items={ItemCount}",
                    batchId, outcome, itemCount)
        } :> Task

    /// Webhook entry for an album photo. Fast path: DB upsert + placeholder
    /// (first photo only) + fire OCR in background + re-arm debounce. Returns
    /// quickly so Telegram can deliver the next album photo.
    member this.HandleAlbumPhoto (user: DbUser) (msg: Message) : Task<bool> =
        task {
            use a = botActivity.StartActivity("handleAlbumPhoto")
            if not (isNull a) then
                %a.SetTag("userId", user.id)
                %a.SetTag("chatId", msg.Chat.Id)

            let mediaGroupId = msg.MediaGroupId
            if String.IsNullOrWhiteSpace mediaGroupId then return false else

            match BotHelpers.getLargestPhotoFileId msg with
            | None -> return false
            | Some photoFileId ->
                let chatId = msg.Chat.Id
                // CreateBatchAtomically holds a per-user advisory lock for the
                // duration of the transaction, so the "wipe single-photo wizard
                // + insert new batch + abandon other active batches" sequence
                // is atomic per user. Two truly-simultaneous album webhooks
                // (different media_group_ids) from the same user will now
                // serialize behind the lock — exactly one batch survives. A
                // concurrent /add command also serialises (its
                // AbandonOpenBatchesExcept takes the same lock).
                let! batchId, isNew, abandoned, ttlReaped =
                    db.CreateBatchAtomically(user.id, mediaGroupId, chatId)

                if isNew then
                    Metrics.batchCreatedTotal.Add(1L)
                    logger.LogInformation(
                        "Batch {BatchId} created for user {UserId} (mgid={MediaGroupId})",
                        batchId, user.id, mediaGroupId)
                if abandoned.Length > 0 then
                    Metrics.batchAbandonedTotal.Add(
                        int64 abandoned.Length,
                        KeyValuePair("reason", box "supersede_album"))
                if ttlReaped > 0 then
                    Metrics.batchAbandonedTotal.Add(
                        int64 ttlReaped,
                        KeyValuePair("reason", box "ttl"))

                for stale in abandoned do
                    if stale.bulk_message_id.HasValue then
                        try
                            do! botClient.EditMessageText(
                                    ChatId stale.bulk_chat_id,
                                    int stale.bulk_message_id.Value,
                                    "Отменено: пришёл новый альбом.")
                                |> taskIgnore
                        with _ -> ()

                if isNew then
                    try
                        let! placeholder =
                            botClient.SendMessage(
                                ChatId chatId,
                                "Получил, обрабатываю купоны, подожди немного…")
                        let! linked = db.SetBatchBulkMessageId(batchId, int64 placeholder.MessageId)
                        if not linked then
                            // Batch was abandoned (by /add, /my, or a fresh album
                            // for a different media_group_id) during the
                            // SendMessage RPC. The placeholder we just sent is an
                            // orphan — clean it up so it doesn't sit in chat.
                            try
                                do! botClient.DeleteMessage(ChatId chatId, placeholder.MessageId)
                            with _ -> ()
                    with ex ->
                        logger.LogWarning(ex, "Failed to send album placeholder for batch {BatchId}", batchId)

                let! itemIdOpt = db.AddBatchItem(batchId, photoFileId, int64 msg.MessageId)
                match itemIdOpt with
                | None ->
                    // Either a Telegram redelivery (same photo_file_id) or the batch
                    // was concurrently abandoned. Either way, nothing more to do.
                    return true
                | Some itemId ->
                    // Fire-and-forget OCR. DB is the channel back to FinalizeBatch.
                    // Capture the current activity context BEFORE Task.Run so the
                    // batchOcrItem span links to handleAlbumPhoto in OTEL traces —
                    // AsyncLocal propagation through Task.Run is unreliable for
                    // fire-and-forget once the parent activity is disposed.
                    let parentCtx =
                        if isNull Activity.Current then None
                        else Some Activity.Current.Context
                    Task.Run(fun () -> this.OcrItem batchId itemId photoFileId parentCtx) |> ignore

                    let debounceMs = botOptions.Value.BatchDebounceMs
                    batchDebounce.Schedule(
                        batchId,
                        debounceMs,
                        Func<Task>(fun () -> this.FinalizeBatch batchId))
                    return true
        }
