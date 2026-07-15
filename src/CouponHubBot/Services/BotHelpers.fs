module CouponHubBot.Services.BotHelpers

open System
open System.Collections.Generic
open System.Threading.Tasks
open Funogram.Telegram.Types
open CouponHubBot
open CouponHubBot.Services
open CouponHubBot.Utils
open BotInfra

module Req = Funogram.Telegram.Req

// ── ITelegramApi call wrappers ──────────────────────────────────────────
// All of these throw TelegramApiException on a Telegram API error (CallExn),
// matching the throwing semantics of the old Telegram.Bot client methods.

/// Sends a text message and returns the sent Message (for callers needing MessageId).
let sendMessage (tg: ITelegramApi) (chatId: int64) (text: string) : Task<Message> =
    tg.CallExn(Req.SendMessage.Make(chatId, text))

let sendText (tg: ITelegramApi) (chatId: int64) (text: string) =
    sendMessage tg chatId text |> taskIgnore

/// Sends a text message with an inline keyboard and returns the sent Message.
let sendMessageMarkup (tg: ITelegramApi) (chatId: int64) (text: string) (kb: InlineKeyboardMarkup) : Task<Message> =
    tg.CallExn(Req.SendMessage.Make(chatId, text, replyMarkup = Markup.InlineKeyboardMarkup kb))

let sendTextMarkup (tg: ITelegramApi) (chatId: int64) (text: string) (kb: InlineKeyboardMarkup) =
    sendMessageMarkup tg chatId text kb |> taskIgnore

/// Sends an HTML-formatted text message.
let sendHtml (tg: ITelegramApi) (chatId: int64) (text: string) =
    tg.CallExn(Req.SendMessage.Make(chatId, text, parseMode = ParseMode.HTML)) |> taskIgnore

/// Sends a text message as a reply to another message (best-effort target:
/// falls back to a plain send server-side when the target is gone).
let sendTextReply (tg: ITelegramApi) (chatId: int64) (text: string) (replyToMessageId: int64) =
    let replyParams = ReplyParameters.Create(replyToMessageId, allowSendingWithoutReply = true)
    tg.CallExn(Req.SendMessage.Make(chatId, text, replyParameters = replyParams)) |> taskIgnore

let editMessageText (tg: ITelegramApi) (chatId: int64) (messageId: int64) (text: string) =
    tg.CallExn(Req.EditMessageText.Make(chatId = ChatId.Int chatId, messageId = messageId, text = text)) |> taskIgnore

let editMessageTextMarkup (tg: ITelegramApi) (chatId: int64) (messageId: int64) (text: string) (kb: InlineKeyboardMarkup) =
    tg.CallExn(Req.EditMessageText.Make(chatId = ChatId.Int chatId, messageId = messageId, text = text, replyMarkup = kb)) |> taskIgnore

let deleteMessage (tg: ITelegramApi) (chatId: int64) (messageId: int64) =
    tg.CallExn(Req.DeleteMessage.Make(chatId, messageId)) |> taskIgnore

/// Sends a photo by Telegram file_id.
let sendPhoto (tg: ITelegramApi) (chatId: int64) (fileId: string) =
    tg.CallExn(Req.SendPhoto.Make(chatId, InputFile.FileId fileId)) |> taskIgnore

/// Sends a photo by file_id with a caption and an inline keyboard.
let sendPhotoWithCaption (tg: ITelegramApi) (chatId: int64) (fileId: string) (caption: string) (kb: InlineKeyboardMarkup) =
    tg.CallExn(Req.SendPhoto.Make(chatId, InputFile.FileId fileId,
                                  caption = caption,
                                  replyMarkup = Markup.InlineKeyboardMarkup kb))
    |> taskIgnore

/// Sends 2-10 photos (by file_id) as one media group.
let sendMediaGroupPhotos (tg: ITelegramApi) (chatId: int64) (fileIds: string array) =
    let media =
        fileIds
        |> Array.map (fun fid -> InputMedia.Photo(InputMediaPhoto.Create("photo", InputFile.FileId fid)))
    tg.CallExn(Req.SendMediaGroup.Make(chatId, media)) |> taskIgnore

// ── Inline keyboard builders ────────────────────────────────────────────

let private btn (text: string) (callbackData: string) =
    InlineKeyboardButton.Create(text, callbackData = callbackData)

let private inlineKb (rows: InlineKeyboardButton[][]) =
    InlineKeyboardMarkup.Create(inlineKeyboard = rows)

/// Short Russian ordinal form used in UI: 1ый, 2ой, 3ий, 4ый, ...
let formatOrdinalShort (n: int) =
    let suffix =
        match n with
        | 2 | 6 | 7 | 8 -> "ой"
        | 3 -> "ий"
        | _ -> "ый"
    $"{n}{suffix}"

let parseInt (s: string) =
    match System.Int32.TryParse(s) with
    | true, v -> Some v
    | _ -> None

let parseDecimalInvariant (s: string) =
    let s2 = s.Trim().Replace(',', '.')
    match Decimal.TryParse(s2, Globalization.NumberStyles.Number, Globalization.CultureInfo.InvariantCulture) with
    | true, v -> Some v
    | _ -> None

let tryParseTwoDecimals (text: string) =
    if String.IsNullOrWhiteSpace text then None
    else
        let t = text.Trim()
        // Support both "X Y" and "X/Y" formats (spaces around '/' are ok).
        if t.Contains("/") then
            let parts = t.Split([| '/' |], StringSplitOptions.RemoveEmptyEntries)
            if parts.Length <> 2 then None
            else
                match parseDecimalInvariant parts[0], parseDecimalInvariant parts[1] with
                | Some a, Some b -> Some(a, b)
                | _ -> None
        else
            let parts = t.Split([| ' '; '\t'; '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
            if parts.Length < 2 then None
            else
                match parseDecimalInvariant parts[0], parseDecimalInvariant parts[1] with
                | Some a, Some b -> Some(a, b)
                | _ -> None

let tryParseDateOnly (time: TimeProvider) (s: string) =
    let styles = System.Globalization.DateTimeStyles.None
    let culture = System.Globalization.CultureInfo.InvariantCulture
    let formats =
        [| "yyyy-MM-dd"
           "yyyy.MM.dd"
           "yyyy/MM/dd"
           "dd.MM.yyyy"
           "d.M.yyyy"
           "dd/MM/yyyy"
           "d/M/yyyy"
           "dd-MM-yyyy"
           "d-M-yyyy" |]
    let mutable parsed = Unchecked.defaultof<DateOnly>
    let t = if isNull s then "" else s.Trim()
    if DateOnly.TryParseExact(t, formats, culture, styles, &parsed) then
        Some parsed
    else
        // Shortcut: allow user to send a single day-of-month number (1..31),
        // and interpret it as the next such day strictly in the future (UTC).
        let isDigitsOnly =
            not (String.IsNullOrWhiteSpace t) && t |> Seq.forall Char.IsDigit

        if isDigitsOnly then
            match Int32.TryParse(t) with
            | true, day when day >= 1 && day <= 31 ->
                let today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime)
                DateUtils.nextDayOfMonthStrictlyFuture today day
            | _ -> None
        else
            None

let formatCouponValue (c: Coupon) =
    let v = c.value.ToString("0.##")
    let mc = c.min_check.ToString("0.##")
    $"{v}€ из {mc}€"

let formatUiDate (d: DateOnly) =
    Utils.DateFormatting.formatDateNoYearWithDow d

let formatAvailableCouponLine (idx: int) (c: Coupon) =
    let d = formatUiDate c.expires_at
    $"{idx}. ID:{c.id} — {formatCouponValue c}, {d}"

let formatEventHistoryTable (rows: CouponEventHistoryRow array) =
    let headers = [| "date"; "user"; "event_type" |]
    let widths =
        headers |> Array.mapi (fun i h ->
            rows |> Array.fold (fun mx r ->
                let v = match i with | 0 -> r.date | 1 -> r.user | _ -> r.event_type
                max mx v.Length) h.Length)
    // System.String qualified: `open Funogram.Telegram.Types` shadows `String` with the ChatId.String DU case.
    let sep = "+" + (widths |> Array.map (fun w -> System.String('-', w)) |> String.concat "+") + "+"
    let fmtRow vals =
        "|" + (Array.zip widths vals |> Array.map (fun (w, v: string) -> v.PadRight(w)) |> String.concat "|") + "|"
    let lines = [
        sep
        fmtRow headers
        sep
        yield! rows |> Array.map (fun r -> fmtRow [| r.date; r.user; r.event_type |])
        sep
    ]
    String.concat "\n" lines

/// Picks coupons for /list:
/// 1) all expiring today (Dublin),
/// 2) at least 2 coupons of min_check=25 (fivers) when available,
///    plus at least 1 of each [40; 50; 100] when available,
/// 3) the result of (1)+(2) may exceed 6 and must not be truncated,
/// 4) if the result is still < 6, fill with the nearest-by-expiry coupons up to 6.
/// Input is expected to be sorted by expires_at, id.
let pickCouponsForList (today: DateOnly) (coupons: Coupon array) =
    if coupons.Length = 0 then
        [||]
    else
        let distinctById (arr: Coupon array) =
            let seen = HashSet<int>()
            arr |> Array.filter (fun c -> seen.Add c.id)

        let expiringToday =
            coupons
            |> Array.filter (fun c -> c.expires_at = today)

        let requiredMinCheckSlots = [| 25m; 25m; 40m; 50m; 100m |]

        let slotPicks =
            let usedIds = HashSet<int>()
            requiredMinCheckSlots
            |> Array.choose (fun mc ->
                match coupons |> Array.tryFind (fun c -> c.min_check = mc && not (usedIds.Contains c.id)) with
                | Some c -> usedIds.Add c.id |> ignore; Some c
                | None -> None)

        let selected =
            Array.append expiringToday slotPicks
            |> distinctById

        let target = min 6 coupons.Length

        let filled =
            if selected.Length >= target then
                selected
            else
                let selectedIds = HashSet<int>(selected |> Array.map (fun c -> c.id))
                let remaining =
                    coupons
                    |> Array.filter (fun c -> not (selectedIds.Contains c.id))

                // When filling up to 6, prefer non-"fivers" first (min_check <> 25),
                // and only then add remaining "fivers" (min_check = 25) if still needed.
                let needed = target - selected.Length
                let remainingNonFivers = remaining |> Array.filter (fun c -> c.min_check <> 25m)
                let remainingFivers = remaining |> Array.filter (fun c -> c.min_check = 25m)

                let fillNonFivers = remainingNonFivers |> Array.truncate needed
                let stillNeeded = needed - fillNonFivers.Length
                let fillFivers =
                    if stillNeeded > 0 then remainingFivers |> Array.truncate stillNeeded
                    else [||]

                Array.append selected (Array.append fillNonFivers fillFivers)

        filled |> Array.sortBy (fun c -> c.expires_at, c.id)

let couponsKeyboard (coupons: Coupon array) =
    coupons
    |> Array.mapi (fun i c -> [| btn $"Взять {formatOrdinalShort (i + 1)}" $"take:{c.id}" |])
    |> inlineKb

let addWizardDiscountKeyboard () =
    inlineKb [|
        [| btn "5€/25€" "addflow:disc:5:25" |]
        [| btn "10€/40€" "addflow:disc:10:40" |]
        [| btn "10€/50€" "addflow:disc:10:50" |]
        [| btn "20€/100€" "addflow:disc:20:100" |]
    |]

let addWizardDateKeyboard () =
    inlineKb [|
        [| btn "Сегодня" "addflow:date:today" |]
        [| btn "Завтра" "addflow:date:tomorrow" |]
    |]

let addWizardOcrKeyboard () =
    inlineKb [|
        [| btn "✅ Да, всё верно" "addflow:ocr:yes"
           btn "Нет, выбрать вручную" "addflow:ocr:no" |]
    |]

let addWizardConfirmKeyboard () =
    inlineKb [|
        [| btn "✅ Добавить" "addflow:confirm"
           btn "↩️ Отмена" "addflow:cancel" |]
    |]

let addBatchConfirmKeyboard (batchId: int64) (okCount: int) =
    if okCount = 0 then
        inlineKb [| [| btn "↩️ Отменить" $"addflow:bulk:cancel:{batchId}" |] |]
    else
        let couponWord = RussianPlural.choose okCount "купон" "купона" "купонов"
        inlineKb [|
            [| btn $"✅ Подтвердить {okCount} {couponWord}" $"addflow:bulk:confirm:{batchId}" |]
            [| btn "↩️ Отменить" $"addflow:bulk:cancel:{batchId}" |]
        |]

/// Клавиатура для сообщения о взятом купоне: при успешном used/return сообщение удаляем.
let singleTakenKeyboard (c: Coupon) =
    inlineKb [|
        [| btn "Вернуть" $"return:{c.id}:del"
           btn "Использован" $"used:{c.id}:del" |]
    |]

let getLargestPhotoFileId (msg: Message) =
    match msg.Photo with
    | Some photos when photos.Length > 0 ->
        let p = photos |> Array.maxBy (fun p -> p.FileSize |> Option.defaultValue 0L)
        Some p.FileId
    | _ -> None

let ensureCommunityMember (membership: TelegramMembershipService) (sendText: int64 -> string -> System.Threading.Tasks.Task<unit>) (userId: int64) (chatId: int64) =
    task {
        let! isMember = membership.IsMember(userId)
        if not isMember then
            do! sendText chatId "Бот доступен только членам сообщества. Если ты точно в чате — напиши /start ещё раз."
        return isMember
    }
