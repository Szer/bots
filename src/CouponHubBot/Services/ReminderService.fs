namespace CouponHubBot.Services

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open BotInfra
open CouponHubBot

type ReminderService(
    tg: ITelegramApi,
    options: IOptions<BotConfiguration>,
    db: DbService,
    logger: ILogger<ReminderService>,
    time: TimeProvider
) =
    inherit BackgroundService()

    let formatUser (userId: int64) (username: string) (firstName: string) =
        if not (String.IsNullOrWhiteSpace username) then
            "@" + username
        elif not (String.IsNullOrWhiteSpace firstName) then
            firstName
        else
            string userId

    // reportedRows carries reports RECEIVED (keyed on the coupon's owner_id, via
    // GetReportedCountsByOwner). The marker is shown only for users who already have a
    // leaderboard line from used/added activity; a user with reports but no used/added
    // history has no line to attach the marker to.
    let formatCombinedStats (usedRows: UserEventCount array) (addedRows: UserEventCount array) (reportedRows: ReportedCountRow array) =
        let usedMap = usedRows |> Array.map (fun r -> r.user_id, r.count) |> Map.ofArray
        let addedMap = addedRows |> Array.map (fun r -> r.user_id, r.count) |> Map.ofArray
        let reportedMap = reportedRows |> Array.map (fun r -> r.user_id, r.count) |> Map.ofArray

        // Collect all users from both arrays, prefer usedRows for user info (username, first_name)
        let userInfoMap =
            Array.append usedRows addedRows
            |> Array.map (fun r -> r.user_id, (r.username, r.first_name))
            |> Map.ofArray

        let allUserIds =
            Set.union
                (usedRows |> Array.map _.user_id |> Set.ofArray)
                (addedRows |> Array.map _.user_id |> Set.ofArray)

        if Set.isEmpty allUserIds then
            "—"
        else
            allUserIds
            |> Set.toArray
            |> Array.map (fun uid ->
                let usedCount = Map.tryFind uid usedMap |> Option.defaultValue 0L
                let addedCount = Map.tryFind uid addedMap |> Option.defaultValue 0L
                let (username, firstName) = Map.find uid userInfoMap
                (uid, username, firstName, usedCount, addedCount))
            |> Array.sortByDescending (fun (_, _, _, used, added) -> used + added)
            |> Array.indexed
            |> Array.map (fun (i, (uid, username, firstName, usedCount, addedCount)) ->
                let n = i + 1
                let who = formatUser uid username firstName
                // Marker shown only when non-zero so the leaderboard doesn't grow a column of zeros.
                let reportedCount = Map.tryFind uid reportedMap |> Option.defaultValue 0L
                let reportSuffix = if reportedCount > 0L then $" ⚠️{reportedCount}" else ""
                $"{n}. {who} — {usedCount}/{addedCount}{reportSuffix}")
            |> String.concat "\n"

    let runOnce (nowUtc: DateTime) =
        task {
            let mutable anySent = false

            let! coupons = db.GetExpiringTodayAvailable()
            if coupons.Length > 0 then
                let total = coupons |> Array.sumBy (fun c -> c.value)
                let totalStr = total.ToString("0.##")
                let couponWord = Utils.RussianPlural.choose coupons.Length "купон" "купона" "купонов"
                let msg = $"Сегодня истекает {coupons.Length} {couponWord} на сумму {totalStr}€!"
                do! tg.CallExn(Funogram.Telegram.Req.SendMessage.Make(options.Value.CommunityChatId, msg)) |> taskIgnore
                anySent <- true

            if nowUtc.DayOfWeek = DayOfWeek.Monday && nowUtc.Day <= 7 then
                // All-time stats: no lower bound (None), not a DateTime.MinValue sentinel.
                let! usedRows = db.GetUserEventCounts("used", None, nowUtc)
                let! addedRows = db.GetUserEventCounts("added", None, nowUtc)
                let! reportedRows = db.GetReportedCountsByOwner(None, nowUtc)

                let text =
                    "Статистика за всё время (использовано/добавлено):\n"
                    + formatCombinedStats usedRows addedRows reportedRows

                do! tg.CallExn(Funogram.Telegram.Req.SendMessage.Make(options.Value.CommunityChatId, text)) |> taskIgnore
                anySent <- true

            // DM reminder: user has taken coupons older than 1 day, still not expired, and forgot
            // to mark used/return. One message per user even if multiple overdue coupons.
            // Coupons that already expired are excluded (DbService.fs: expires_at >= today) —
            // there's nothing actionable left to do with those via /my.
            let! overdueRows = db.GetUsersWithOverdueTakenCoupons(nowUtc, TimeSpan.FromDays(1.0))
            let overdueByUser = overdueRows |> Array.groupBy (fun r -> r.user_id)
            for (userId, coupons) in overdueByUser do
                try
                    let count = coupons.Length
                    let couponWord = Utils.RussianPlural.choose count "купон" "купона" "купонов"
                    let participle = if count = 1 then "взятый" else "взятых"
                    let notMarked = if count = 1 then "не отмеченный" else "не отмеченных"
                    let couponIds = coupons |> Array.map (fun c -> c.id)
                    let couponLines =
                        coupons
                        |> Array.map (fun c ->
                            let v = c.value.ToString("0.##")
                            let mc = c.min_check.ToString("0.##")
                            let d = Utils.DateFormatting.formatDateNoYearWithDow c.expires_at
                            $"Купон ID:{c.id} на {v}€ из {mc}€, до {d}")
                        |> String.concat "\n"
                    let text =
                        $"Напоминание: у тебя есть {count} {couponWord}, {participle} более 1 дня назад и всё ещё {notMarked}.\n{couponLines}\nОткрой /my и нажми «Использован» или «Вернуть»."
                    logger.LogInformation("Sending overdue-taken reminder to {UserId} for coupons {CouponIds}", userId, couponIds)
                    do! tg.CallExn(Funogram.Telegram.Req.SendMessage.Make(userId, text)) |> taskIgnore
                    anySent <- true
                with ex ->
                    logger.LogWarning(ex, "Failed to send overdue-taken reminder to {UserId}", userId)

            // DM reminder: user used coupon yesterday but did not add any coupon on the same day.
            // One message per user.
            let! usersWhoUsedButDidNotAdd = db.GetUsersWhoUsedButDidNotAddYesterday(nowUtc)
            for userId in usersWhoUsedButDidNotAdd do
                try
                    let text = "Не забудь добавить купоны в бота"
                    do! tg.CallExn(Funogram.Telegram.Req.SendMessage.Make(userId, text)) |> taskIgnore
                    anySent <- true
                with ex ->
                    logger.LogWarning(ex, "Failed to send add-coupon reminder to {UserId}", userId)

            // Retention cleanup: delete community chat messages older than 1 year.
            try
                let oneYearAgo = nowUtc.AddYears(-1)
                let! deleted = db.DeleteOldChatMessages(oneYearAgo)
                if deleted > 0 then
                    logger.LogInformation("Deleted {Count} chat messages older than 1 year", deleted)
            with ex ->
                logger.LogWarning(ex, "Failed to clean up old chat messages")

            return anySent
        }

    let nextRunUtc (hourDublin: int) =
        let now = time.GetUtcNow().UtcDateTime
        let dublinTz = Utils.TimeZones.getDublinTimeZone()
        let nowDublin = TimeZoneInfo.ConvertTimeFromUtc(now, dublinTz)
        let todayAtHourDublin = DateTime(nowDublin.Year, nowDublin.Month, nowDublin.Day, hourDublin, 0, 0)
        let targetDublin =
            if nowDublin <= todayAtHourDublin then todayAtHourDublin
            else todayAtHourDublin.AddDays(1.0)
        TimeZoneInfo.ConvertTimeToUtc(targetDublin, dublinTz)

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            if options.Value.ReminderRunOnStart then
                try
                    let! _ = runOnce (time.GetUtcNow().UtcDateTime)
                    ()
                with ex ->
                    logger.LogError(ex, "Failed to run reminder on startup")

            while not stoppingToken.IsCancellationRequested do
                let next = nextRunUtc options.Value.ReminderHourDublin
                let delay = next - time.GetUtcNow().UtcDateTime
                if delay > TimeSpan.Zero then
                    logger.LogInformation("Next reminder run at {NextRunUtc}", next)
                    do! Task.Delay(delay, stoppingToken)

                if stoppingToken.IsCancellationRequested then
                    ()
                else
                    try
                        let! _ = runOnce(time.GetUtcNow().UtcDateTime)
                        ()
                    with ex ->
                        logger.LogError(ex, "Failed to send reminder")
        }

    member _.RunOnce(?nowUtc: DateTime) =
        let now = defaultArg nowUtc (time.GetUtcNow().UtcDateTime)
        runOnce now
