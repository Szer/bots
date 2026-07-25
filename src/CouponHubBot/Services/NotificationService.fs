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
    /// failure here must not fail the report itself (docs/PLAN-report-used-coupon.md §6).
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
