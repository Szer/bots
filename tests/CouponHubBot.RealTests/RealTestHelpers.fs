namespace CouponHubBot.RealTests

open System
open System.Globalization
open System.IO
open System.Text.RegularExpressions
open System.Threading.Tasks

/// Cross-cutting real-test helpers that need BOTH DbSeed (DB) and TgUserClient/
/// RealAssemblyFixture (Telegram) — the centrepiece being `addCouponViaCaptionAsync`,
/// which the "shared plumbing" task exists to hand the four expansion-suite authors so
/// none of them has to re-derive it. Kept out of TgUserClient.fs (a deliberate
/// near-verbatim COPY of AlitaBot.RealTests/TgUserClient.fs — see its own doc comment on
/// why edits there are minimized to Telegram-only, fixture-agnostic plumbing) and out of
/// DbSeed.fs (admin-connection DB-only helpers). See README.md for the full inventory.
module RealTestHelpers =

    /// Ground-truth barcode for each fixture in tests/CouponHubBot.Ocr.Tests/Images/,
    /// read off the filename's own encoding: OcrTests.fs's
    /// `Parsing.parseExpectedFromFileName` documents the convention as
    /// "[couponValue]_[minCheck]_[validFrom]_[validTo]_[barcode].jpg" (OcrTests.fs:166-178),
    /// and its `` `OCR engine recognizes coupon from file` `` theory (OcrTests.fs:216-232)
    /// asserts `CouponOcrEngine.Recognize` reproduces that exact 5th field as `res.barcode`
    /// for every filename below EXCEPT the last two — these are NOT invented values, they
    /// are read directly off each fixture's own name.
    ///
    /// The last two entries ("low quality") come from OcrTests.fs's separate
    /// `` `OCR engine recognizes coupon from low quality file` `` theory
    /// (OcrTests.fs:275-286), which deliberately passes `null` for `expectedBarcode` on
    /// both — i.e. reliable barcode extraction from these two specific photos is NOT
    /// proven the way it is for the other sixteen. They're listed here anyway (same
    /// filename-encoded value, for completeness/consistency), but a caller reaching for
    /// a "definitely idempotent" fixture should prefer one of the other sixteen; if OCR
    /// mis-extracts (or fails to extract) a barcode from either of these two on a given
    /// run, `deleteCouponsByBarcodeAsync` — keyed on the filename-encoded value below —
    /// would not find and clear that run's coupon row.
    let fixtureBarcodes: Map<string, string> =
        Map.ofList [
            "10_50_01-04_01-13_2706602781191.jpg", "2706602781191"
            "10_50_01-06_01-15_2706643333717.jpg", "2706643333717"
            "10_50_01-12_01-21_2706513420233.jpg", "2706513420233"
            "10_50_01-12_01-21_2706530490622.jpg", "2706530490622"
            "10_50_01-14_01-23_2706658654210.jpg", "2706658654210"
            "10_50_01-19_01-28_2706613152454.jpg", "2706613152454"
            "10_50_01-21_01-30_2706616470579.jpg", "2706616470579"
            "5_25_01-15_01-21_2706528422291.jpg", "2706528422291"
            "5_25_01-15_01-21_2706666377231.jpg", "2706666377231"
            "5_25_01-15_01-21_2706684806638.jpg", "2706684806638"
            "5_25_2026-02-08_2026-02-14_2706726228947.jpg", "2706726228947"
            "5_25_2026-04-05_2026-04-15_2706873139950.jpg", "2706873139950"
            "10_50_2026-01-11_2026-01-20_2706678568818.jpg", "2706678568818"
            "10_50_2026-01-17_2026-01-26_2706688198838.jpg", "2706688198838"
            "10_50_2026-01-17_2026-01-26_2706688198845.jpg", "2706688198845"
            "10_50_2026-01-17_2026-01-26_2706688198821.jpg", "2706688198821"
            // Low-quality pair — barcode NOT proven reliable by OcrTests.fs, see doc comment above.
            "5_25_2026-01-11_2026-01-17_2706653336241.jpg", "2706653336241"
            "5_25_2026-01-20_2026-01-26_2706680353051.jpg", "2706680353051"
        ]

    /// A `yyyy-MM-dd` date `daysFromNow` days out. DbService.TryAddCoupon rejects
    /// `expires_at < todayUtc()` (DbService.fs:198-200) and every fixture image is
    /// deliberately dated in the past (public-repo constraint — see AddFlowRealTests.fs's
    /// doc comment), so every /add through a fixture needs its expiry overridden via the
    /// caption's explicit date, same trick every existing add-flow real test already uses.
    let futureExpiry (daysFromNow: float) : string =
        DateTime.UtcNow.AddDays(daysFromNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

    /// Matches the coupon id out of the bot's "Добавлен купон ID:<n>: ..." confirmation
    /// (the exact template — CallbackHandler.fs:284,329 and CouponFlowHandler.fs:166).
    let private couponIdInConfirmation = Regex(@"ID:(\d+)", RegexOptions.Compiled)

    /// THE add helper (contract, Deliverable 1 "An add helper — the centrepiece"). Sends
    /// `fixtureImage` (a key of `fixtureBarcodes`) as a photo with an explicit
    /// "/add <value> <minCheck> <date>" caption, awaits the "Добавлен купон ID:<n>: ..."
    /// confirmation, and returns <n> parsed straight out of that reply text (no DB round
    /// trip needed — DbSeed.getLatestOwnedCouponIdAsync remains available separately for
    /// tests that add via other means, e.g. ReminderRealTests.fs's direct-SQL insert).
    ///
    /// Retry-safe / fixture-reusable (the whole point of this task — contract's "problem"
    /// section): deletes any pre-existing coupon row for this fixture's barcode FIRST via
    /// DbSeed.deleteCouponsByBarcodeAsync. Without this: (a) TestRetry's one automatic
    /// retry after an AwaitTimeoutException would re-send the identical fixture photo
    /// into coupon_barcode_active_uniq's partial unique index and turn a flaky timeout
    /// into a hard, NON-retried assertion failure ("Купон с таким штрихкодом уже есть в
    /// базе…"); (b) the expansion suite reuses these same 16-18 fixtures across many more
    /// tests than there are distinct barcodes for.
    ///
    /// `expiryDate` defaults to 365 days out (`futureExpiry 365.`) when `None` — pass
    /// `Some <yyyy-MM-dd>` only when the test itself is exercising expiry-dependent
    /// behavior. (`?`-optional-argument syntax is member-only in F# — FS0718 — hence the
    /// explicit `option` here rather than the brief's suggested bare positional arg.)
    let addCouponViaCaptionAsync
        (fx: RealAssemblyFixture)
        (fixtureImage: string)
        (value: string)
        (minCheck: string)
        (expiryDate: string option)
        : Task<int> =
        task {
            let barcode =
                match fixtureBarcodes.TryFind fixtureImage with
                | Some b -> b
                | None ->
                    failwith
                        $"'{fixtureImage}' has no known barcode in RealTestHelpers.fixtureBarcodes — add it there (read the barcode off the fixture's OWN filename, see OcrTests.fs's naming convention; do not invent one) before calling addCouponViaCaptionAsync with it"

            do! DbSeed.deleteCouponsByBarcodeAsync fx.DbConnectionString barcode

            let imagePath = Path.Combine(RealEnv.ocrFixtureImagesDir, fixtureImage)
            if not (File.Exists imagePath) then
                failwith $"Expired-coupon fixture missing: {imagePath}"

            let expiry = expiryDate |> Option.defaultWith (fun () -> futureExpiry 365.)
            let! sentId = fx.UserClient.SendPhoto(fx.BotChatId, imagePath, $"/add {value} {minCheck} {expiry}")
            let! reply = fx.UserClient.AwaitTextContaining(fx.BotChatId, sentId, "Добавлен купон", TimeSpan.FromSeconds 90.)

            let m = couponIdInConfirmation.Match reply.message
            if not m.Success then
                failwith $"Could not parse a coupon id out of the add confirmation text: '{reply.message}'"

            return int m.Groups[1].Value
        }
