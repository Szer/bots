module CouponHubBot.Services.BotHelpers

open System
open System.Collections.Generic
open System.Threading.Tasks
open Microsoft.Extensions.Logging
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

/// Sends an HTML-formatted text message with an inline keyboard (/balances' first page).
let sendHtmlMarkup (tg: ITelegramApi) (chatId: int64) (text: string) (kb: InlineKeyboardMarkup) =
    tg.CallExn(Req.SendMessage.Make(chatId, text, parseMode = ParseMode.HTML, replyMarkup = Markup.InlineKeyboardMarkup kb)) |> taskIgnore

/// Sends a text message as a reply to another message (best-effort target:
/// falls back to a plain send server-side when the target is gone).
let sendTextReply (tg: ITelegramApi) (chatId: int64) (text: string) (replyToMessageId: int64) =
    let replyParams = ReplyParameters.Create(replyToMessageId, allowSendingWithoutReply = true)
    tg.CallExn(Req.SendMessage.Make(chatId, text, replyParameters = replyParams)) |> taskIgnore

let editMessageText (tg: ITelegramApi) (chatId: int64) (messageId: int64) (text: string) =
    tg.CallExn(Req.EditMessageText.Make(chatId = ChatId.Int chatId, messageId = messageId, text = text)) |> taskIgnore

let editMessageTextMarkup (tg: ITelegramApi) (chatId: int64) (messageId: int64) (text: string) (kb: InlineKeyboardMarkup) =
    tg.CallExn(Req.EditMessageText.Make(chatId = ChatId.Int chatId, messageId = messageId, text = text, replyMarkup = kb)) |> taskIgnore

/// Edits a message's text (HTML parse mode) and keyboard — /balances' ◀/▶/sort in-place page flips.
let editHtmlMarkup (tg: ITelegramApi) (chatId: int64) (messageId: int64) (text: string) (kb: InlineKeyboardMarkup) =
    tg.CallExn(Req.EditMessageText.Make(chatId = ChatId.Int chatId, messageId = messageId, text = text, parseMode = ParseMode.HTML, replyMarkup = kb)) |> taskIgnore

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

let formatCouponValue (value: decimal) (minCheck: decimal) =
    let v = value.ToString("0.##")
    let mc = minCheck.ToString("0.##")
    $"{v}€ из {mc}€"

let formatUiDate (d: DateOnly) =
    Utils.DateFormatting.formatDateNoYearWithDow d

let formatAvailableCouponLine (idx: int) (c: Coupon) =
    let d = formatUiDate c.expires_at
    $"{idx}. ID:{c.id} — {formatCouponValue c.value c.min_check}, {d}"

/// "@username", else first_name, else the raw numeric id. Matches the precedent in
/// ReminderService's private formatUser, first introduced for the §8a leaderboard.
let formatUserHandle (userId: int64) (username: string | null) (firstName: string | null) =
    if not (String.IsNullOrWhiteSpace username) then "@" + username
    elif not (String.IsNullOrWhiteSpace firstName) then firstName
    else string userId

/// Escapes the three characters Telegram's HTML parse mode treats as markup, so free-form
/// text (barcodes, names) can never break — or inject into — a message we send as HTML.
let htmlEscape (s: string | null) =
    if isNull s then ""
    else s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")

/// Admin /debug dump of a coupon: every stored field except the photo file id.
/// Dates are rendered ISO (not the UI's day-of-week form) — this is a debugging view,
/// so exactness beats prettiness. Timestamps are UTC, as stored.
let formatCouponDebugDetails (c: Coupon) (owner: DbUser option) (taker: DbUser option) =
    let inv = System.Globalization.CultureInfo.InvariantCulture
    let isoDate (d: DateOnly) = d.ToString("yyyy-MM-dd", inv)
    let isoTime (t: DateTime) = t.ToString("yyyy-MM-dd HH:mm:ss", inv)
    let userLine (userId: int64) (u: DbUser option) =
        match u with
        | Some u -> $"{htmlEscape (formatUserHandle u.id u.username u.first_name)} (id:{userId})"
        | None -> $"id:{userId}"
    let lines = [
        $"<b>Купон ID:{c.id}</b>"
        $"Номинал: {formatCouponValue c.value c.min_check}"
        "Действует с: " + (if c.valid_from.HasValue then isoDate c.valid_from.Value else "—")
        $"Годен до: {isoDate c.expires_at}"
        "Штрихкод: " + (if String.IsNullOrWhiteSpace c.barcode_text then "—" else htmlEscape c.barcode_text)
        $"Статус: {c.status}"
        $"Владелец: {userLine c.owner_id owner}"
        if c.taken_by.HasValue then
            let takenAt =
                if c.taken_at.HasValue then $", {isoTime c.taken_at.Value} UTC" else ""
            $"Взят: {userLine c.taken_by.Value taker}{takenAt}"
        $"Добавлен: {isoTime c.created_at} UTC"
    ]
    String.concat "\n" lines

/// Bounded Levenshtein distance between two already-normalized tokens.
let private levenshtein (a: string) (b: string) =
    let n, m = a.Length, b.Length
    let dp = Array2D.create (n + 1) (m + 1) 0
    for i in 0 .. n do dp[i, 0] <- i
    for j in 0 .. m do dp[0, j] <- j
    for i in 1 .. n do
        for j in 1 .. m do
            let cost = if a[i - 1] = b[j - 1] then 0 else 1
            dp[i, j] <- min (min (dp[i - 1, j] + 1) (dp[i, j - 1] + 1)) (dp[i - 1, j - 1] + cost)
    dp[n, m]

/// Similarity in [0,1] between a query token and a candidate name/username token: exact/
/// substring hits score high, else a Levenshtein ratio (below 0.4 counts as no match at all).
let private tokenSimilarity (t: string) (c: string) =
    if t = "" || c = "" then 0.0
    elif t = c then 1.0
    elif t.Length >= 2 && (c.Contains(t) || t.Contains(c)) then 0.85
    else
        let d = levenshtein t c
        let sim = 1.0 - float d / float (max t.Length c.Length)
        if sim > 0.4 then sim else 0.0

/// Id matching is substring-only — Levenshtein "typo tolerance" isn't meaningful for an
/// opaque numeric id, and applying it lets an unrelated query coincidentally out-score
/// the threshold against someone else's id once there are enough users to compare against.
let private idSimilarity (t: string) (idStr: string) =
    if t = idStr then 1.0
    elif t.Length >= 2 && idStr.Contains(t) then 0.85
    else 0.0

/// Lowercased, whitespace-split query tokens for the /whois fuzzy fallback.
let private whoisQueryTokens (query: string) =
    query.Trim().ToLowerInvariant().Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.toList

/// Normalized username/name tokens of a user, matched against a fuzzy /whois query.
let private whoisNameTokens (u: DbUser) =
    [ Option.ofObj u.username |> Option.map (fun s -> s.Trim().ToLowerInvariant())
      Option.ofObj u.first_name |> Option.map (fun s -> s.Trim().ToLowerInvariant())
      Option.ofObj u.last_name |> Option.map (fun s -> s.Trim().ToLowerInvariant()) ]
    |> List.choose id
    |> List.filter (fun s -> s <> "")

/// Candidates scoring at or above this average are shown by the /whois fuzzy fallback.
let whoisFuzzyThreshold = 0.35

/// Average, over query tokens, of each token's best similarity against a user's id
/// (substring-only) or username/name tokens (fuzzy). Pure and unit-testable — no DB access.
let scoreWhoisCandidate (query: string) (u: DbUser) =
    let qTokens = whoisQueryTokens query
    let nameTokens = whoisNameTokens u
    let idStr = string u.id
    if qTokens.IsEmpty then
        0.0
    else
        qTokens
        |> List.map (fun t ->
            let idScore = idSimilarity t idStr
            let nameScore =
                if nameTokens.IsEmpty then 0.0
                else nameTokens |> List.map (tokenSimilarity t) |> List.max
            max idScore nameScore)
        |> List.average

/// The /whois fuzzy fallback: candidates above whoisFuzzyThreshold, best first, capped at 5.
let searchWhoisFuzzy (query: string) (users: DbUser array) =
    users
    |> Array.map (fun u -> u, scoreWhoisCandidate query u)
    |> Array.filter (fun (_, score) -> score >= whoisFuzzyThreshold)
    |> Array.sortByDescending snd
    |> Array.truncate 5
    |> Array.map fst

/// Admin /whois identity + membership + all-time give/take header. Value is
/// NUMERIC(10,2) NOT NULL, so sums are always real numbers — no "unparsed" caveat needed.
let formatWhoisHeader (u: DbUser) (isMember: bool) (stats: UserContributionStats) =
    let inv = System.Globalization.CultureInfo.InvariantCulture
    let isoDate (d: DateTime) = d.ToString("yyyy-MM-dd", inv)
    let euro (v: decimal) = v.ToString("0.##", inv) + "€"
    let fullName =
        [ Option.ofObj u.first_name; Option.ofObj u.last_name ] |> List.choose id |> String.concat " "
    let nameSuffix = if String.IsNullOrWhiteSpace fullName then "" else $", {htmlEscape fullName}"
    let memberLine = if isMember then "да" else "нет"
    let lines = [
        $"<b>{htmlEscape (formatUserHandle u.id u.username u.first_name)}</b> (id:{u.id}){nameSuffix}"
        $"В базе с: {isoDate u.created_at}"
        $"В комьюнити: {memberLine}"
        ""
        $"Добавлено: {stats.added_count} · {euro stats.added_value}"
        $"Взято: {stats.taken_count} · {euro stats.taken_value}"
        $"Баланс: {stats.added_count - stats.taken_count} · {euro (stats.added_value - stats.taken_value)}"
    ]
    String.concat "\n" lines

/// Plain-text 3-column table for /whois's "last 10 actions" <pre> block.
let formatWhoisActionsTable (rows: UserActionRow array) =
    let couponDesc (r: UserActionRow) =
        if r.value.HasValue && r.min_check.HasValue then
            $"ID:{r.coupon_id} {formatCouponValue r.value.Value r.min_check.Value}"
        else
            $"ID:{r.coupon_id}"
    let headers = [| "date"; "event"; "coupon" |]
    let widths =
        headers |> Array.mapi (fun i h ->
            rows |> Array.fold (fun mx r ->
                let v = match i with | 0 -> r.date | 1 -> r.event_type | _ -> couponDesc r
                max mx v.Length) h.Length)
    let sep = "+" + (widths |> Array.map (fun w -> System.String('-', w)) |> String.concat "+") + "+"
    let fmtRow vals =
        "|" + (Array.zip widths vals |> Array.map (fun (w, v: string) -> v.PadRight(w)) |> String.concat "|") + "|"
    let lines = [
        sep
        fmtRow headers
        sep
        yield! rows |> Array.map (fun r -> fmtRow [| r.date; r.event_type; couponDesc r |])
        sep
    ]
    String.concat "\n" lines

/// /whois fuzzy fallback list: one "id:<id> @<username> <first> <last>" line per candidate
/// (username part omitted when NULL). Callers only reach here when the list is non-empty.
let formatWhoisCandidateList (users: DbUser array) =
    let line (u: DbUser) =
        let namePart =
            [ Option.ofObj u.first_name; Option.ofObj u.last_name ] |> List.choose id |> String.concat " "
        let userPart = if String.IsNullOrWhiteSpace u.username then "" else $" @{htmlEscape u.username}"
        let namePartHtml = if namePart = "" then "" else $" {htmlEscape namePart}"
        $"id:{u.id}{userPart}{namePartHtml}"
    let lines = "Найдены похожие юзеры:" :: (users |> Array.map line |> Array.toList)
    String.concat "\n" lines

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

// ── /balances (admin all-user coupon balance report) ────────────────────

/// Page size for /balances.
let balancesPageSize = 15

/// Fixed width for the name column — keeps rows narrow regardless of name length.
let balancesNameWidth = 18

/// Callback-data token per sort mode; round-robins balance → added → taken → balance.
let balancesSortToken (m: BalanceSortMode) =
    match m with
    | BalanceSortMode.Balance -> "balance"
    | BalanceSortMode.Added -> "added"
    | BalanceSortMode.Taken -> "taken"

/// Unknown/garbled tokens default to Balance rather than failing the callback.
let balancesSortFromToken (s: string) =
    match s with
    | "added" -> BalanceSortMode.Added
    | "taken" -> BalanceSortMode.Taken
    | _ -> BalanceSortMode.Balance

let balancesNextSort (m: BalanceSortMode) =
    match m with
    | BalanceSortMode.Balance -> BalanceSortMode.Added
    | BalanceSortMode.Added -> BalanceSortMode.Taken
    | BalanceSortMode.Taken -> BalanceSortMode.Balance

let balancesSortLabel (m: BalanceSortMode) =
    match m with
    | BalanceSortMode.Balance -> "баланс"
    | BalanceSortMode.Added -> "залил"
    | BalanceSortMode.Taken -> "взял"

let private truncateFixed (w: int) (s: string) =
    if s.Length <= w then s.PadRight(w)
    else s.Substring(0, w - 1) + "…"

let private balancesUserName (r: UserBalanceRow) =
    if not (String.IsNullOrWhiteSpace r.username) then "@" + r.username
    else
        let full = [ Option.ofObj r.first_name; Option.ofObj r.last_name ] |> List.choose id |> String.concat " "
        if full = "" then string r.user_id else full

let private balancesCell (cnt: int64) (v: decimal) =
    let vStr = v.ToString("0.##")
    $"{cnt}·{vStr}€"

/// Markdown-style table (header + `|---|` separator) for /balances' <pre> block.
/// `nameWidth` is a parameter (not the balancesNameWidth constant) so the message-size
/// guard in formatBalancesMessage can shrink it for a pathological page.
let formatBalancesTable (nameWidth: int) (rows: UserBalanceRow array) =
    let idOf (r: UserBalanceRow) = string r.user_id
    let addedOf (r: UserBalanceRow) = balancesCell r.added_count r.added_value
    let takenOf (r: UserBalanceRow) = balancesCell r.taken_count r.taken_value
    let balanceOf (r: UserBalanceRow) = balancesCell (r.added_count - r.taken_count) (r.added_value - r.taken_value)
    let hId, hUser, hAdded, hTaken, hBalance = "id", "user", "залил", "взял", "баланс"
    let wId = rows |> Array.fold (fun mx r -> max mx (idOf r).Length) hId.Length
    let wAdded = rows |> Array.fold (fun mx r -> max mx (addedOf r).Length) hAdded.Length
    let wTaken = rows |> Array.fold (fun mx r -> max mx (takenOf r).Length) hTaken.Length
    let wBalance = rows |> Array.fold (fun mx r -> max mx (balanceOf r).Length) hBalance.Length
    // System.String qualified: `open Funogram.Telegram.Types` shadows `String` with the ChatId.String DU case.
    // +2 per column: each data cell sits between a leading/trailing space inside its "| … |" segment.
    let sep = "|" + ([ wId; nameWidth; wAdded; wTaken; wBalance ] |> List.map (fun w -> System.String('-', w + 2)) |> String.concat "|") + "|"
    let fmtRow (idCell: string) (userCell: string) (addedCell: string) (takenCell: string) (balanceCell: string) =
        "| " + ([ idCell.PadRight(wId); truncateFixed nameWidth userCell; addedCell.PadRight(wAdded); takenCell.PadRight(wTaken); balanceCell.PadRight(wBalance) ] |> String.concat " | ") + " |"
    let lines = [
        fmtRow hId hUser hAdded hTaken hBalance
        sep
        yield! rows |> Array.map (fun r -> fmtRow (idOf r) (balancesUserName r) (addedOf r) (takenOf r) (balanceOf r))
    ]
    String.concat "\n" lines

/// "Стр. X/Y · всего: N · сортировка: <mode>" — the plain status line above /balances' table.
let formatBalancesStatusLine (page: int) (totalPages: int) (totalCount: int64) (sort: BalanceSortMode) =
    $"Стр. {page}/{totalPages} · всего: {totalCount} · сортировка: {balancesSortLabel sort}"

/// Full /balances message: status line + <pre> table. Falls back to a narrower name column
/// if the default rendering would exceed Telegram's 4096-char cap (15 rows never should, but
/// this is a cheap guard rather than elaborate truncation machinery).
let formatBalancesMessage (rows: UserBalanceRow array) (statusLine: string) =
    let build nameWidth = $"{statusLine}\n<pre>{htmlEscape (formatBalancesTable nameWidth rows)}</pre>"
    let full = build balancesNameWidth
    if full.Length <= 4096 then full else build 6

/// ◀ / sort-cycle / ▶ row. ◀ omitted on page 1, ▶ omitted on the last page. The sort
/// button shows the CURRENT mode; its callback data carries the NEXT mode and resets to page 1.
let balancesKeyboard (page: int) (totalPages: int) (sort: BalanceSortMode) =
    let sortToken = balancesSortToken sort
    let nextToken = balancesSortToken (balancesNextSort sort)
    let prevBtn = if page > 1 then Some(btn "◀" $"balances:{page - 1}:{sortToken}") else None
    let sortBtn = Some(btn $"⇅ {balancesSortLabel sort}" $"balances:1:{nextToken}")
    let nextBtn = if page < totalPages then Some(btn "▶" $"balances:{page + 1}:{sortToken}") else None
    let row = [ prevBtn; sortBtn; nextBtn ] |> List.choose id |> List.toArray
    inlineKb [| row |]

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

/// Visibility status of an owner's coupon relative to /list, used by /added to explain
/// why an "available" coupon may or may not currently be shown.
[<RequireQualifiedAccess>]
type CouponListStatus =
    | Shown
    | Taken
    | Reported
    | NotYetValid of validFrom: DateOnly
    | Waiting of ahead: int * minCheck: decimal
    | WaitingGeneric
    /// Defensive: "available" but absent from the /list candidate pool for a reason other
    /// than valid_from — should be unreachable given how GetAvailableCoupons/
    /// GetVoidableCouponsByOwner are queried, but never crash /added over it.
    | WaitingUnknown

/// How many /list slots a denomination's dedicated queue has: 2 for the "fivers" (min_check=25),
/// 1 each for min_check 40/50/100, 0 for anything else (those only ever get in via fill-to-6).
let private bucketSlotCapacity (minCheck: decimal) =
    if minCheck = 25m then 2
    elif minCheck = 40m || minCheck = 50m || minCheck = 100m then 1
    else 0

/// Determines an owner's coupon's /added visibility status, mirroring pickCouponsForList's
/// logic so the verdict always matches what /list actually shows.
/// `pool` is the same coupon set /list picks from (db.GetAvailableCoupons()), `shown` is
/// pickCouponsForList today pool.
let couponListStatus (today: DateOnly) (pool: Coupon array) (shown: Coupon array) (c: Coupon) : CouponListStatus =
    match c.status with
    | "taken" -> CouponListStatus.Taken
    | "reported" -> CouponListStatus.Reported
    | "available" ->
        if c.valid_from.HasValue && c.valid_from.Value > today then
            CouponListStatus.NotYetValid c.valid_from.Value
        elif shown |> Array.exists (fun s -> s.id = c.id) then
            CouponListStatus.Shown
        elif not (pool |> Array.exists (fun p -> p.id = c.id)) then
            CouponListStatus.WaitingUnknown
        elif bucketSlotCapacity c.min_check = 0 then
            CouponListStatus.WaitingGeneric
        else
            let ahead =
                pool
                |> Array.filter (fun p -> p.min_check = c.min_check)
                |> Array.sortBy (fun p -> p.expires_at, p.id)
                |> Array.findIndex (fun p -> p.id = c.id)
            CouponListStatus.Waiting(ahead, c.min_check)
    | _ -> CouponListStatus.WaitingUnknown

/// Renders couponListStatus as a parenthetical suffix, in the style of the existing
/// " (взят)"/" (отмечен использованным)" strings. Never repeats the expiry date —
/// the /added line already prints expires_at.
let formatCouponListStatusSuffix (status: CouponListStatus) : string =
    match status with
    | CouponListStatus.Shown -> " (в списке)"
    | CouponListStatus.Taken -> " (взят)"
    | CouponListStatus.Reported -> " (отмечен использованным)"
    | CouponListStatus.NotYetValid vf -> $" (начнёт действовать с {formatUiDate vf})"
    | CouponListStatus.Waiting(0, _) ->
        // ahead=0 but still not Shown is a contradictory-looking state (its own dedicated slot
        // should have picked it up) — fall back to the generic wording rather than claim "0 ahead".
        " (в очереди, точно будет в списке в день истечения)"
    | CouponListStatus.Waiting(ahead, mc) ->
        let word = Utils.RussianPlural.choose ahead "купон" "купона" "купонов"
        let mcStr = mc.ToString("0.##")
        $" (в очереди: впереди {ahead} {word} из {mcStr}€, точно будет в списке в день истечения)"
    | CouponListStatus.WaitingGeneric -> " (в очереди, точно будет в списке в день истечения)"
    | CouponListStatus.WaitingUnknown -> " (в очереди)"

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

/// "Какой купон уже использован?" step: one button per currently-held coupon plus «Отмена».
let reportSelectKeyboard (heldCouponIds: int array) =
    let rows = heldCouponIds |> Array.map (fun id -> [| btn $"ID:{id}" $"report:{id}" |])
    inlineKb (Array.append rows [| [| btn "Отмена" "reportCancel" |] |])

/// Confirmation step before a report is filed — deliberately requires an extra tap since a
/// report accuses another member.
let reportConfirmKeyboard (couponId: int) =
    inlineKb [|
        [| btn "Подтвердить" $"report:{couponId}:confirm" |]
        [| btn "Отмена" "reportCancel" |]
    |]

let getLargestPhotoFileId (msg: Message) =
    match msg.Photo with
    | Some photos when photos.Length > 0 ->
        let p = photos |> Array.maxBy (fun p -> p.FileSize |> Option.defaultValue 0L)
        Some p.FileId
    | _ -> None

let ensureCommunityMember (membership: TelegramMembershipService) (logger: ILogger) (sendText: int64 -> string -> System.Threading.Tasks.Task<unit>) (userId: int64) (chatId: int64) =
    task {
        let! isMember = membership.IsMember(userId)
        if not isMember then
            logger.LogWarning("Membership denied for user {UserId}", userId)
            do! sendText chatId "Бот доступен только членам сообщества. Если ты точно в чате — напиши /start ещё раз."
        return isMember
    }
