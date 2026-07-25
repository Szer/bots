namespace CouponHubBot.RealTests

open System
open System.Globalization
open System.IO
open Dapper
open Npgsql
open Xunit

/// Coverage item 8 (contract): "/report <id> -> coupon leaves pool, adder DM'd (new
/// feature, PR #267)."
///
/// Same single-account caveat as CouponLifecycleRealTests: the "adder" and the
/// "reporter" are the same real Telegram account here, so both the reporter's own
/// confirmation ("отправлен владельцу...") and the adder-facing DM
/// ("сообщил, что купон ID:... уже был использован.", NotificationService.fs:37-39)
/// land in the same private chat — this test asserts on BOTH distinct texts rather
/// than on which chat each landed in (which is how the hermetic ReportFlowTests.fs
/// tells them apart, using two different synthetic user ids).
type ReportFlowRealTests(fx: RealAssemblyFixture) =

    let futureExpiry = DateTime.UtcNow.AddDays(200.).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

    [<Fact>]
    member _.``report command removes the coupon from the pool and DMs the adder``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let imagePath = Path.Combine(RealEnv.ocrFixtureImagesDir, "10_50_01-14_01-23_2706658654210.jpg")
            Assert.True(File.Exists imagePath, $"Expired-coupon fixture missing: {imagePath}")

            let! addSentId = fx.UserClient.SendPhoto(fx.BotChatId, imagePath, $"/add 10 50 {futureExpiry}")
            let! _addReply = fx.UserClient.AwaitTextContaining(fx.BotChatId, addSentId, "Добавлен купон", TimeSpan.FromSeconds 90.)

            use conn = new NpgsqlConnection(fx.DbConnectionString)
            let! couponId =
                conn.QuerySingleAsync<int>(
                    "SELECT id FROM coupon WHERE owner_id=@o ORDER BY id DESC LIMIT 1",
                    {| o = fx.UserClient.Me.id |})

            let! takeSentId = fx.UserClient.SendText(fx.BotChatId, $"/take {couponId}")
            let! _taken = fx.UserClient.AwaitPhotoCaptionContaining(fx.BotChatId, takeSentId, "теперь твой", TimeSpan.FromSeconds 60.)

            let! reportSentId = fx.UserClient.SendText(fx.BotChatId, $"/report {couponId}")

            let! confirmReply =
                fx.UserClient.AwaitTextContaining(fx.BotChatId, reportSentId, "отправлен владельцу", TimeSpan.FromSeconds 60.)
            Assert.Contains($"ID:{couponId}", confirmReply.message)

            let! adderDm =
                fx.UserClient.AwaitTextContaining(fx.BotChatId, reportSentId, "уже был использован", TimeSpan.FromSeconds 60.)
            Assert.Contains($"ID:{couponId}", adderDm.message)

            let! status = conn.QuerySingleAsync<string>("SELECT status FROM coupon WHERE id=@id", {| id = couponId |})
            Assert.Equal("reported", status)
        })
