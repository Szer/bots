namespace CouponHubBot

open System
open System.Globalization
open BotInfra

module Utils =
    module DateUtils =
        /// Find the next date (strictly after `today`) that has given day-of-month.
        /// Skips months that don't contain the day (e.g. 31 in April).
        let nextDayOfMonthStrictlyFuture (today: DateOnly) (dayOfMonth: int) : DateOnly option =
            if dayOfMonth < 1 || dayOfMonth > 31 then
                None
            else
                // Search forward month-by-month (bounded to avoid infinite loops).
                let startMonth = DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc)

                let rec loop monthOffset =
                    if monthOffset > 24 then
                        None
                    else
                        let dt = startMonth.AddMonths(monthOffset)
                        try
                            let candidate = DateOnly(dt.Year, dt.Month, dayOfMonth)
                            if candidate > today then Some candidate else loop (monthOffset + 1)
                        with _ ->
                            loop (monthOffset + 1)

                loop 0

    module DateFormatting =
        let private ru = CultureInfo("ru-RU")

        /// User-facing date format: day + full month name + full day-of-week, no year.
        /// Example: "22 января, четверг"
        let formatDateNoYearWithDow (d: DateOnly) =
            d.ToString("d MMMM, dddd", ru)

    module TimeZones =
        let private tryFind (id: string) =
            try
                Some(TimeZoneInfo.FindSystemTimeZoneById(id))
            with _ ->
                None

        // NOTE:
        // - Linux containers typically have IANA TZ IDs (e.g. "Europe/Dublin")
        // - Windows typically uses Windows TZ IDs (e.g. "GMT Standard Time")
        let private dublinTzLazy =
            lazy (
                match tryFind "Europe/Dublin" with
                | Some tz -> tz
                | None ->
                    match tryFind "GMT Standard Time" with
                    | Some tz -> tz
                    | None -> TimeZoneInfo.Utc
            )

        let getDublinTimeZone () = dublinTzLazy.Value

        /// Returns "today" date in Europe/Dublin (derived from TimeProvider UTC now).
        let dublinToday (time: TimeProvider) =
            let nowUtc = time.GetUtcNow().UtcDateTime
            let local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, getDublinTimeZone ())
            DateOnly.FromDateTime(local)

    module RussianPlural =
        /// Returns appropriate Russian plural form based on the number.
        /// form1 = 1, 21, 31... | form2 = 2-4, 22-24... | form5 = 0, 5-20, 25-30...
        let choose (n: int) (form1: string) (form2: string) (form5: string) =
            let n100 = abs n % 100
            let n10 = abs n % 10
            if n100 >= 11 && n100 <= 14 then form5
            elif n10 = 1 then form1
            elif n10 >= 2 && n10 <= 4 then form2
            else form5

    /// Funogram type helpers.
    module Tg =
        open Funogram.Telegram.Types

        /// Extracts the wire status string ("creator" | "administrator" | "member" |
        /// "restricted" | "left" | "kicked") from a ChatMember, e.g. for telemetry.
        let chatMemberStatus (member': ChatMember) : string =
            match member' with
            | ChatMember.Owner m -> m.Status
            | ChatMember.Administrator m -> m.Status
            | ChatMember.Member m -> m.Status
            | ChatMember.Restricted m -> m.Status
            | ChatMember.Left m -> m.Status
            | ChatMember.Banned m -> m.Status

        /// The affected user of a ChatMember — every case carries one.
        let chatMemberUser (member': ChatMember) : User =
            match member' with
            | ChatMember.Owner m -> m.User
            | ChatMember.Administrator m -> m.User
            | ChatMember.Member m -> m.User
            | ChatMember.Restricted m -> m.User
            | ChatMember.Left m -> m.User
            | ChatMember.Banned m -> m.User

        /// Chat + message id of the message a callback query originated from,
        /// handled uniformly for accessible and inaccessible messages.
        let callbackMessage (cq: CallbackQuery) : (Chat * int64) option =
            cq.Message
            |> Option.map (function
                | MaybeInaccessibleMessage.Message m -> m.Chat, m.MessageId
                | MaybeInaccessibleMessage.InaccessibleMessage m -> m.Chat, m.MessageId)
