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
