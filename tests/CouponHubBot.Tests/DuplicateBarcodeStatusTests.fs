namespace CouponHubBot.Tests

open BotTestInfra
open Xunit
open FakeCallHelpers
open BatchTestHelpers

/// Regression coverage for TryAddCoupon's primary duplicate-barcode check
/// (DbService.fs, ~line 231): it used to ignore coupon.status entirely, so a
/// voided or used coupon with a future expires_at permanently blocked
/// re-adding the same barcode — contradicting coupon_barcode_active_uniq's
/// partial-unique predicate (V18__coupon_reported_status.sql), which only
/// covers status IN ('available', 'taken', 'reported'). Fixed by adding the
/// same status filter already used by the sibling 23505 race-recovery
/// lookup a few lines below in DbService.fs.
type DuplicateBarcodeStatusTests(fixture: OcrCouponHubTestContainers) =

    // Barcode is encoded in the filename; OCR/ZXing decodes it from the real image bytes.
    let fileName = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
    let barcode = "2706688198845"
    // Matches this fixture image's own OCR-derived expires_at (2026-01-26).
    let sameExpiresIso = "2026-01-26"

    let getCouponCount () =
        fixture.QuerySingle<int64>("SELECT COUNT(*)::bigint FROM coupon", null)

    /// Directly seeds an owner + a coupon row with a chosen status/expiry, bypassing
    /// TryAddCoupon entirely — this is the pre-existing row the new add attempt will
    /// (or won't) collide with.
    let seedOwnerAndCoupon (ownerId: int64) (status: string) (expiresIso: string) =
        task {
            let! _ =
                fixture.Execute(
                    """
INSERT INTO "user" (id, username, first_name, last_name, updated_at)
VALUES (@owner_id, @username, 'Seed', NULL, now())
ON CONFLICT (id) DO NOTHING
""",
                    {| owner_id = ownerId; username = $"seed_{ownerId}" |})
            let! _ =
                fixture.Execute(
                    """
INSERT INTO coupon (owner_id, photo_file_id, value, min_check, expires_at, barcode_text, status)
VALUES (@owner_id, @photo_file_id, 10, 50, @expires_at::date, @barcode_text, @status)
""",
                    {| owner_id = ownerId
                       photo_file_id = $"seed-photo-{ownerId}"
                       expires_at = expiresIso
                       barcode_text = barcode
                       status = status |})
            ()
        }

    [<Fact>]
    let ``Voided coupon with future expiry does not block re-adding same barcode`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            do! seedOwnerAndCoupon 98001L "voided" sameExpiresIso

            let user = Tg.user(id = 6901L, username = "dup_status_voided", firstName = "Voided")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let fileId = "dup-status-voided"
            do! fixture.SetTelegramFile(fileId, readImageBytes fileName)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson fileName)
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("", user, fileId = fileId))
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback("addflow:ocr:yes", user))

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls user.Id "Добавлен купон",
                "Expected the add to succeed despite the same barcode being on a voided coupon")

            let! count = getCouponCount ()
            Assert.Equal(2L, count)
        }

    [<Fact>]
    let ``Used coupon with future expiry does not block re-adding same barcode`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            do! seedOwnerAndCoupon 98002L "used" sameExpiresIso

            let user = Tg.user(id = 6902L, username = "dup_status_used", firstName = "Used")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let fileId = "dup-status-used"
            do! fixture.SetTelegramFile(fileId, readImageBytes fileName)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson fileName)
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("", user, fileId = fileId))
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback("addflow:ocr:yes", user))

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls user.Id "Добавлен купон",
                "Expected the add to succeed despite the same barcode being on a used coupon")

            let! count = getCouponCount ()
            Assert.Equal(2L, count)
        }

    [<Fact>]
    let ``Available coupon with same barcode still blocks add`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            do! seedOwnerAndCoupon 98003L "available" sameExpiresIso

            let user = Tg.user(id = 6903L, username = "dup_status_available", firstName = "Avail")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let fileId = "dup-status-available"
            do! fixture.SetTelegramFile(fileId, readImageBytes fileName)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson fileName)
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("", user, fileId = fileId))
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback("addflow:ocr:yes", user))

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls user.Id "уже есть в базе",
                "Expected the add to still be rejected as a duplicate barcode (available coupon)")

            let! count = getCouponCount ()
            Assert.Equal(1L, count)
        }

    [<Fact>]
    let ``Taken coupon with same barcode still blocks add`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            do! seedOwnerAndCoupon 98004L "taken" sameExpiresIso

            let user = Tg.user(id = 6904L, username = "dup_status_taken", firstName = "Taken")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let fileId = "dup-status-taken"
            do! fixture.SetTelegramFile(fileId, readImageBytes fileName)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson fileName)
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("", user, fileId = fileId))
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback("addflow:ocr:yes", user))

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls user.Id "уже есть в базе",
                "Expected the add to still be rejected as a duplicate barcode (taken coupon)")

            let! count = getCouponCount ()
            Assert.Equal(1L, count)
        }

    [<Fact>]
    let ``Reported coupon with same barcode still blocks add`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            do! seedOwnerAndCoupon 98005L "reported" sameExpiresIso

            let user = Tg.user(id = 6905L, username = "dup_status_reported", firstName = "Reported")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let fileId = "dup-status-reported"
            do! fixture.SetTelegramFile(fileId, readImageBytes fileName)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson fileName)
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("", user, fileId = fileId))
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback("addflow:ocr:yes", user))

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls user.Id "уже есть в базе",
                "Expected the add to still be rejected as a duplicate barcode (reported coupon)")

            let! count = getCouponCount ()
            Assert.Equal(1L, count)
        }

    /// Pins the deliberate divergence from coupon_barcode_active_uniq's exact-expiry
    /// match: the primary check keeps `expires_at >= @today` (any future date), not
    /// exact equality, so it still catches the same barcode re-added under a
    /// different future expiry — the index alone would not block this case.
    [<Fact>]
    let ``Active coupon with a different future expires_at for same barcode still blocks add`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            // Pre-existing row expires far later than the new add's own OCR-derived
            // date (2026-01-26) — an exact-match check (like the unique index) would
            // NOT see these as colliding, but the primary check's `>=` breadth does.
            do! seedOwnerAndCoupon 98006L "available" "2026-06-01"

            let user = Tg.user(id = 6906L, username = "dup_status_diff_expiry", firstName = "DiffExp")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let fileId = "dup-status-diff-expiry"
            do! fixture.SetTelegramFile(fileId, readImageBytes fileName)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson fileName)
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("", user, fileId = fileId))
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback("addflow:ocr:yes", user))

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls user.Id "уже есть в базе",
                "Expected the broader >= @today check to still reject a same-barcode add under a different future expiry")

            let! count = getCouponCount ()
            Assert.Equal(1L, count)
        }
