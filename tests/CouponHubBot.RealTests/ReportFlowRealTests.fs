namespace CouponHubBot.RealTests

open System
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
///
/// Root-caused (run 30181924500, failure 1): this test used to hand-roll
/// SendPhoto + "/add" directly, with no delete-by-barcode-first step. Its final status
/// is 'reported' — an ACTIVE status under both `coupon_barcode_active_uniq`'s partial
/// index AND (this is the actual trigger) DbService.TryAddCoupon's primary duplicate-
/// barcode check (DbService.fs:231-238), which — unlike its own race-condition recovery
/// path a few lines below (DbService.fs:296-305) — does NOT filter by status at all, so
/// it also treats a `voided`/`used`/`returned` row for the same barcode+future-expiry as
/// a blocking duplicate. In run 30181924500, MyAndAddedRealTests' "added lists..." test
/// ran first, added+voided a coupon for this SAME fixture's barcode (2706658654210,
/// expiry 365 days out), and that VOIDED row then wrongly blocked this test's own
/// (non-deleting) /add on both the original attempt and TestRetry's one retry — see
/// `RealTestHelpers.addCouponViaCaptionAsync`'s doc comment for the general shape of this
/// hazard. Switched to that idempotent helper (deletes any existing row for the fixture's
/// barcode FIRST, regardless of status) so this test no longer depends on what any other
/// test using the same fixture left behind. The underlying DbService.fs:231-238 status-
/// blind duplicate check is a genuine production bug — reported separately, not fixed
/// here (out of scope: src/ changes need a human decision per this task's brief).
type ReportFlowRealTests(fx: RealAssemblyFixture) =

    [<Fact>]
    member _.``report command removes the coupon from the pool and DMs the adder``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let! couponId =
                RealTestHelpers.addCouponViaCaptionAsync
                    fx
                    "10_50_01-14_01-23_2706658654210.jpg"
                    "10"
                    "50"
                    (Some(RealTestHelpers.futureExpiry 200.))

            use conn = new NpgsqlConnection(fx.DbConnectionString)

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
