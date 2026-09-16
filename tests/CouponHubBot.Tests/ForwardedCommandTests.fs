namespace CouponHubBot.Tests

open BotTestInfra
open Dapper
open Npgsql
open Xunit
open FakeCallHelpers
open BatchTestHelpers
open Funogram.Telegram.Types

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

    let getCouponCount () =
        fixture.QuerySingle<int64>("SELECT COUNT(*)::bigint FROM coupon", null)

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

    // ── Forwarded photos must keep working — spouses forward each other coupon photos ──
    // (HandleAddManual reads msg.Caption, not msg.Text, so the guard above never sees them.)

    [<Fact>]
    let ``Forwarded photo with /add caption is still added as a coupon`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let member_ = Tg.user(id = 9805L, username = "fwd_photo_add", firstName = "Member")
            do! fixture.SetChatMemberStatus(member_.Id, "member")

            let forwarded =
                Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", member_, forwardOrigin = Tg.forwardedFromUser())
            let! resp = fixture.SendUpdate(forwarded)
            Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode)

            let! count = getCouponCount ()
            Assert.Equal(1L, count)
            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls member_.Id "Добавлен купон",
                "Forwarded photo with an /add caption should add a coupon exactly like a non-forwarded one")
        }

    [<Fact>]
    let ``Forwarded photo without caption still starts the add wizard`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let member_ = Tg.user(id = 9806L, username = "fwd_photo_bare", firstName = "Member")
            do! fixture.SetChatMemberStatus(member_.Id, "member")

            let forwarded = Tg.dmPhotoWithCaption("", member_, forwardOrigin = Tg.forwardedFromUser())
            let! _ = fixture.SendUpdate(forwarded)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls member_.Id "Выбери скидку",
                "A bare forwarded photo with no pending flow should start the add wizard, same as a non-forwarded one")
        }

/// Album batch flow (HandleAlbumPhoto) reads msg.MediaGroupId/msg.Photo, never Caption/Text,
/// and doesn't gate on forward status — same OCR fixture as BatchAddFlowTests is required.
type ForwardedAlbumTests(fixture: OcrCouponHubTestContainers) =

    let getCouponCount () =
        fixture.QuerySingle<int64>("SELECT COUNT(*)::bigint FROM coupon", null)

    /// Drives an album to the bulk-confirm prompt (mirrors BatchAddFlowTests' "Album of 3 OK").
    /// `imageFiles` must be distinct per caller — shared images collide on DuplicateBarcode.
    let runAlbumToConfirm (user: User) (mgid: string) (imageFiles: string list) (forwardOrigin: MessageOrigin option) =
        task {
            let files = imageFiles |> List.mapi (fun i fn -> $"fwd-album-{user.Id}-{i}", fn)
            for fid, fn in files do
                do! fixture.SetTelegramFile(fid, readImageBytes fn)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson (snd files[0]))

            for fid, _ in files do
                let! _ = fixture.SendUpdate(Tg.dmAlbumPhoto(user, mgid, fileId = fid, ?forwardOrigin = forwardOrigin))
                ()

            let! batchId = waitForBatchByUser fixture user.Id 5000
            do! waitForAllItemsTerminal fixture batchId 10000
            do! advancePastDebounce fixture
            do! waitForBatchStatus fixture batchId "awaiting_user" 5000
            do! waitForBulkConfirmCall fixture user.Id 5000
            return batchId
        }

    [<Fact>]
    let ``Forwarded album behaves exactly like a non-forwarded album`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            do! fixture.TruncateBatches()
            do! resetOcrFakes fixture

            let plainUser = Tg.user(id = 9900L, username = "fwd_album_plain", firstName = "Plain")
            let fwdUser = Tg.user(id = 9901L, username = "fwd_album_fwd", firstName = "Forwarded")
            do! fixture.SetChatMemberStatus(plainUser.Id, "member")
            do! fixture.SetChatMemberStatus(fwdUser.Id, "member")

            // Distinct images per run: each image's baked-in barcode drives TryAddCoupon's
            // global dedup, so the plain and forwarded runs must not share a barcode.
            let plainImages = [ "10_50_2026-01-17_2026-01-26_2706688198821.jpg"; "10_50_2026-01-17_2026-01-26_2706688198838.jpg" ]
            let fwdImages = [ "10_50_2026-01-17_2026-01-26_2706688198845.jpg"; "10_50_2026-01-11_2026-01-20_2706678568818.jpg" ]
            let! plainBatchId = runAlbumToConfirm plainUser $"mg-plain-{plainUser.Id}" plainImages None
            let! fwdBatchId = runAlbumToConfirm fwdUser $"mg-fwd-{fwdUser.Id}" fwdImages (Some(Tg.forwardedFromUser()))

            let! calls = fixture.GetFakeCalls("sendMessage")
            let plainConfirm = bulkConfirmCalls calls plainUser.Id
            let fwdConfirm = bulkConfirmCalls calls fwdUser.Id
            Assert.Equal(1, plainConfirm.Length)
            Assert.Equal(1, fwdConfirm.Length)
            Assert.True(findCallWithText calls plainUser.Id "Подтвердить 2 купона", "Plain album should offer to confirm 2 coupons")
            Assert.True(findCallWithText calls fwdUser.Id "Подтвердить 2 купона", "Forwarded album should offer to confirm 2 coupons, same as plain")

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback($"addflow:bulk:confirm:{plainBatchId}", plainUser))
            let! _ = fixture.SendUpdate(Tg.dmCallback($"addflow:bulk:confirm:{fwdBatchId}", fwdUser))
            do! waitForBatchCleared fixture plainBatchId 5000
            do! waitForBatchCleared fixture fwdBatchId 5000

            let! count = getCouponCount ()
            Assert.Equal(4L, count)
        }
