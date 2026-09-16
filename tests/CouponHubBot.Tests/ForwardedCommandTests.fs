namespace CouponHubBot.Tests

open BotTestInfra
open Dapper
open Npgsql
open Xunit
open FakeCallHelpers

/// #498: a forwarded old /undo group message replayed into the bot DM reverted someone
/// else's legitimate action (coupon 1534). Forwarded text must never execute as a command.
type ForwardedCommandTests(fixture: DefaultCouponHubTestContainers) =

    let adminId = 900L

    let getLatestCouponId () =
        task {
            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            //language=postgresql
            return! conn.QuerySingleAsync<int>("SELECT id FROM coupon ORDER BY id DESC LIMIT 1")
        }

    let getStatus (couponId: int) =
        fixture.QuerySingle<string>("SELECT status FROM coupon WHERE id = @id", {| id = couponId |})

    let getEventCount (couponId: int) =
        fixture.QuerySingle<int64>(
            "SELECT COUNT(*)::bigint FROM coupon_event WHERE coupon_id = @id",
            {| id = couponId |})

    [<Fact>]
    let ``Forwarded undo from an admin changes nothing and appends no event`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 9800L, username = "fwd_undo_owner", firstName = "Owner")
            let taker = Tg.user(id = 9801L, username = "fwd_undo_taker", firstName = "Taker")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")
            do! fixture.SetChatMemberStatus(adminId, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/used {couponId}", taker))

            let! eventsBefore = getEventCount couponId

            do! fixture.ClearFakeCalls()
            let admin = Tg.user(id = adminId, username = "admin", firstName = "Admin")
            let forwarded = Tg.dmMessage($"/undo {couponId}", admin, forwardOrigin = Tg.forwardedFromUser())
            let! _ = fixture.SendUpdate(forwarded)

            let! status = getStatus couponId
            Assert.Equal("used", status)
            let! eventsAfter = getEventCount couponId
            Assert.Equal(eventsBefore, eventsAfter)
        }

    [<Fact>]
    let ``Forwarded used from a regular member leaves the coupon status unchanged`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 9802L, username = "fwd_used_owner", firstName = "Owner")
            let taker = Tg.user(id = 9803L, username = "fwd_used_taker", firstName = "Taker")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))

            do! fixture.ClearFakeCalls()
            let forwarded = Tg.dmMessage($"/used {couponId}", taker, forwardOrigin = Tg.forwardedFromUser())
            let! _ = fixture.SendUpdate(forwarded)

            let! status = getStatus couponId
            Assert.Equal("taken", status)
        }

    [<Fact>]
    let ``Forwarded command in a private chat gets a one-line rejection reply`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let taker = Tg.user(id = 9804L, username = "fwd_reply_taker", firstName = "Taker")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            let forwarded = Tg.dmMessage("/used 1", taker, forwardOrigin = Tg.forwardedFromUser())
            let! _ = fixture.SendUpdate(forwarded)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls taker.Id "Пересланные сообщения не выполняются как команды",
                "Forwarded command in a private chat should get the one-line rejection")
        }
