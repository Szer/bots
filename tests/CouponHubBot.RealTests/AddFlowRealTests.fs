namespace CouponHubBot.RealTests

open System
open System.Globalization
open System.IO
open Dapper
open Npgsql
open Xunit

[<CLIMutable>]
type CouponRow =
    { id: int
      value: decimal
      min_check: decimal
      expires_at: DateOnly
      barcode_text: string | null }

/// Coverage item 2 (contract): "/add with photo + OCR prefill -> wizard -> confirm ->
/// coupon exists in DB."
///
/// Photo fixture (contract, BINDING): reused from tests/CouponHubBot.Ocr.Tests/Images/,
/// which is entirely made of EXPIRED coupon photos — this repo is PUBLIC, so a coupon
/// photo with a live, currently-usable barcode must never be committed here.
/// `5_25_01-15_01-21_2706528422291.jpg` is the closest match in that folder to the
/// contract's suggested filename (barcode 2706528422291); ITS EXPIRY MUST NEVER BE
/// REFRESHED TO A CURRENT DATE — doing so would publish a live, usable barcode in a
/// public repo. See src/CouponHubBot/docs/OCR.md for the fixture-naming convention.
///
/// The caption below carries value/min-check/expiry EXPLICITLY (contract: "The /add
/// caption carries value/min-check/expiry explicitly ... OCR only extracts the
/// barcode") rather than sending a bare "/add" and letting OCR prefill the wizard —
/// letting OCR read the image's own (expired, in-the-past) printed date would either
/// get the add rejected as "already expired" or create an unusable coupon, neither of
/// which coverage items 3-8/10 could then exercise. OCR_ENABLED=true is still seeded
/// (contract's bot_setting table), so the single-shot add below still exercises a real
/// Azure OCR call server-side for barcode extraction — verified below by asserting
/// `barcode_text` landed in the DB — even though the interactive OCR-prefill wizard UI
/// itself isn't the thing under test here.
type AddFlowRealTests(fx: RealAssemblyFixture) =

    let futureExpiry = DateTime.UtcNow.AddDays(365.).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

    let getLatestCouponForOwner (ownerId: int64) =
        task {
            use conn = new NpgsqlConnection(fx.DbConnectionString)
            return!
                conn.QuerySingleAsync<CouponRow>(
                    "SELECT id, value, min_check, expires_at, barcode_text FROM coupon WHERE owner_id = @owner_id ORDER BY id DESC LIMIT 1",
                    {| owner_id = ownerId |})
        }

    [<Fact>]
    member _.``add with photo and explicit caption creates a coupon, OCR extracts a barcode``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let imagePath = Path.Combine(RealEnv.ocrFixtureImagesDir, "5_25_01-15_01-21_2706528422291.jpg")
            Assert.True(File.Exists imagePath, $"Expired-coupon fixture missing: {imagePath}")

            let caption = $"/add 5 25 {futureExpiry}"
            let! sentId = fx.UserClient.SendPhoto(fx.BotChatId, imagePath, caption)
            let! _reply = fx.UserClient.AwaitTextContaining(fx.BotChatId, sentId, "Добавлен купон", TimeSpan.FromSeconds 90.)

            let! coupon = getLatestCouponForOwner fx.UserClient.Me.id
            Assert.Equal(5m, coupon.value)
            Assert.Equal(25m, coupon.min_check)
            Assert.Equal(futureExpiry, coupon.expires_at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            Assert.False(String.IsNullOrWhiteSpace coupon.barcode_text, "Expected OCR to extract a barcode from the real Azure Vision call")
        })
