namespace CouponHubBot.Services

open System.Threading.Tasks
open Microsoft.Extensions.Logging
open BotInfra
open CouponHubBot

type TelegramNotificationService(
    tg: ITelegramApi,
    logger: ILogger<TelegramNotificationService>
) =

    member _.NotifyTakerCouponVoided(takerUserId: int64, coupon: Coupon) : Task<bool> =
        task {
            let v = coupon.value.ToString("0.##")
            let mc = coupon.min_check.ToString("0.##")
            let msg = $"Купон ID:{coupon.id} ({v}€/{mc}€) был аннулирован владельцем. Он больше недоступен."
            try
                do! tg.CallExn(Funogram.Telegram.Req.SendMessage.Make(takerUserId, msg)) |> taskIgnore
                return true
            with ex1 ->
                logger.LogWarning(ex1, "First attempt to notify taker {TakerId} about voided coupon {CouponId} failed, retrying", takerUserId, coupon.id)
                try
                    do! Task.Delay(500)
                    do! tg.CallExn(Funogram.Telegram.Req.SendMessage.Make(takerUserId, msg)) |> taskIgnore
                    return true
                with ex2 ->
                    logger.LogError(ex2, "Failed to notify taker {TakerId} about voided coupon {CouponId} after retry", takerUserId, coupon.id)
                    return false
        }

    /// Unsolicited DM to the adder when a holder reports their coupon as already used
    /// externally. Same single-retry-and-report-failure shape as NotifyTakerCouponVoided —
    /// failure here must not fail the report itself.
    member _.NotifyAdderCouponReported(ownerUserId: int64, coupon: Coupon, reporterHandle: string) : Task<bool> =
        task {
            let msg =
                $"Пользователь {reporterHandle} сообщил, что купон ID:{coupon.id} уже был использован.\n"
                + "Если вы использовали его вне бота — нажмите «Использован» в /my или аннулируйте в /added."
            try
                do! tg.CallExn(Funogram.Telegram.Req.SendMessage.Make(ownerUserId, msg)) |> taskIgnore
                return true
            with ex1 ->
                logger.LogWarning(ex1, "First attempt to notify owner {OwnerId} about reported coupon {CouponId} failed, retrying", ownerUserId, coupon.id)
                try
                    do! Task.Delay(500)
                    do! tg.CallExn(Funogram.Telegram.Req.SendMessage.Make(ownerUserId, msg)) |> taskIgnore
                    return true
                with ex2 ->
                    logger.LogError(ex2, "Failed to notify owner {OwnerId} about reported coupon {CouponId} after retry", ownerUserId, coupon.id)
                    return false
        }

    /// Unsolicited DM to the member whose pocket an admin /undo moved a coupon into or out of.
    /// Without it the rewind is invisible to them: they keep waiting for a coupon that is back
    /// in their own hands (or keep counting on one that has returned to the pool). Same
    /// single-retry-and-report-failure shape as the notifications above — a delivery failure
    /// must not fail the undo itself, which is already committed by the time we get here.
    member _.NotifyUndoPocketChange(userId: int64, coupon: Coupon, revertedEvent: string, change: UndoPocketChange) : Task<bool> =
        task {
            let v = coupon.value.ToString("0.##")
            let mc = coupon.min_check.ToString("0.##")
            let until = Utils.DateFormatting.formatDateNoYearWithDow coupon.expires_at
            let what = $"ID:{coupon.id} ({v}€ из {mc}€, до {until})"
            let action =
                match revertedEvent with
                | "used" -> "отметку «Использован»"
                | "returned" -> "возврат"
                | "reported" -> "жалобу «уже использован кем-то»"
                | "taken" -> "взятие"
                | other -> $"действие «{other}»"
            let msg =
                match change with
                | UndoPocketChange.BackToPocket _ ->
                    $"Админ откатил {action} по купону {what}.\n"
                    + "Купон снова у тебя — он лежит в /my: пометь «Использован», когда используешь, или верни его в общую копилку кнопкой «Вернуть»."
                | UndoPocketChange.RemovedFromPocket _ ->
                    $"Админ откатил {action} по купону {what}.\n"
                    + "Купон больше не числится за тобой и вернулся в общую копилку."
                // Nobody's pocket moved — nothing to tell anyone.
                | UndoPocketChange.Untouched -> ""

            if msg = "" then
                return true
            else
                try
                    do! tg.CallExn(Funogram.Telegram.Req.SendMessage.Make(userId, msg)) |> taskIgnore
                    return true
                with ex1 ->
                    logger.LogWarning(ex1, "First attempt to notify user {UserId} about undo of '{Event}' on coupon {CouponId} failed, retrying", userId, revertedEvent, coupon.id)
                    try
                        do! Task.Delay(500)
                        do! tg.CallExn(Funogram.Telegram.Req.SendMessage.Make(userId, msg)) |> taskIgnore
                        return true
                    with ex2 ->
                        logger.LogError(ex2, "Failed to notify user {UserId} about undo of '{Event}' on coupon {CouponId} after retry", userId, revertedEvent, coupon.id)
                        return false
        }
