namespace CouponHubBot.RealTests

open System
open System.Globalization
open System.IO
open System.Threading.Tasks
open Dapper
open Npgsql
open Xunit
open TL

/// Dapper-mappable row for `latestCouponForOwner` below — a named record (not an
/// anonymous record) because Dapper's default QuerySingleAsync<T> mapping needs
/// settable properties, which F# anonymous records don't provide.
[<CLIMutable>]
type private WizardCouponRow =
    { id: int
      value: decimal
      min_check: decimal }

/// Coverage: the interactive `/add` wizard (bare "/add" -> photo -> discount/date/confirm)
/// and the separate album-flow `addflow:bulk:cancel:<batchId>` button.
///
/// ── The state machine, as determined from the source (all 16 "good" fixtures in
/// tests/CouponHubBot.Ocr.Tests/Images/ — everything except the two "low quality" ones —
/// make OCR recognize barcode+value+min_check+valid_to reliably; see OcrTests.fs:216-232
/// and RealTestHelpers.fs's fixtureBarcodes doc comment) ──
///
/// 1. Bare "/add" -> `HandleAddWizardStart` (CouponFlowHandler.fs:72-97) sets
///    stage="awaiting_photo" and asks for a photo.
/// 2. A bare (caption-less) photo at that stage -> `TryHandleWizardMessage`
///    (CouponFlowHandler.fs:383-388) -> `HandleAddWizardPhoto`
///    (CouponFlowHandler.fs:179-349). Because every fixture yields a full OCR match
///    (barcode + value + min_check + valid_to all present), this ALWAYS lands on
///    stage="awaiting_ocr_confirm" (CouponFlowHandler.fs:289-308) with `expires_at` set
///    to OCR's own (past, printed-on-the-photo) date, and shows the
///    `addflow:ocr:yes` / `addflow:ocr:no` keyboard.
/// 3. `addflow:ocr:no` (CallbackHandler.fs:296-309) moves to
///    stage="awaiting_discount_choice" and — critically — EXPLICITLY CLEARS
///    `expires_at` back to `Nullable()` (CallbackHandler.fs:303). This is what keeps
///    the expired-fixture trap described in this file's originating brief from firing:
///    every subsequent `addflow:disc:*` press (CallbackHandler.fs:203-231) checks
///    `flow.expires_at.HasValue`, which is now false, so it ALWAYS lands on
///    stage="awaiting_date_choice" (CouponFlowHandler.fs:351-354) rather than jumping
///    straight to confirm with the stale past date. The date step is therefore always
///    reached on this route, and `addflow:date:today` / `addflow:date:tomorrow`
///    (CallbackHandler.fs:232-257) write a FRESH, non-expired `expires_at` before the
///    confirm screen — so `addflow:confirm` (CallbackHandler.fs:310-340) always calls
///    `TryAddCoupon` with a valid date and there is a genuine happy path. No SQL
///    workaround (unlike BulkAddRealTests.fs's `bumpItemsToFutureExpiry`) is needed
///    here — `addflow:ocr:no` already does the equivalent for the interactive wizard.
///
/// ── `addflow:ocr:yes` — reachable only via a documented SQL workaround (see the test
/// below for the full citation) ──
/// `addflow:ocr:yes` (CallbackHandler.fs:258-293) calls `TryAddCoupon` directly with
/// `flow.expires_at.Value` — the OCR-derived date, which for every fixture in this
/// public repo is deliberately in the past (repo-public constraint, see
/// AddFlowRealTests.fs's doc comment). `DbService.TryAddCoupon` rejects
/// `expires_at < todayUtc()` unconditionally (DbService.fs:198-200) BEFORE any DB
/// write, so `addflow:ocr:yes` against an UNMODIFIED fixture can only ever reach the
/// "Нельзя добавить истёкший купон" rejection branch (CallbackHandler.fs:285-287) —
/// never `AddCouponResult.Added`. There is no photo in this fixture set whose printed
/// date is in the future (every fixture is intentionally expired), so the Added-branch
/// happy path can only be reached by overwriting the PENDING WIZARD ROW's own
/// `pending_add.expires_at` via direct SQL after OCR lands and before pressing "yes" —
/// the same trick BulkAddRealTests.fs's `bumpItemsToFutureExpiry` already uses for the
/// album path's `pending_add_batch_item.expires_at`. See the test below for why this is
/// safe (it overrides the wizard's OWN staged expiry, not the fixture photo) and why it
/// must never be "cleaned up".
type AddWizardRealTests(fx: RealAssemblyFixture) =

    let pollInterval = TimeSpan.FromMilliseconds 500.

    /// Local copy of BulkAddRealTests.fs's `waitFor` (private to that file's type, not
    /// reusable across files per this task's ownership boundary) — generic DB-poll
    /// helper for the bulk-cancel test below.
    let waitFor (description: string) (timeout: TimeSpan) (check: unit -> Task<'a option>) =
        task {
            let deadline = DateTime.UtcNow + timeout
            let mutable result = None
            while result.IsNone && DateTime.UtcNow < deadline do
                let! r = check ()
                result <- r
                if result.IsNone then do! Task.Delay pollInterval
            match result with
            | Some v -> return v
            | None -> return raise (AwaitTimeoutException $"Timed out waiting for {description} after {timeout.TotalSeconds}s")
        }

    let waitForBatchByOwner (ownerId: int64) =
        waitFor "pending_add_batch row" (TimeSpan.FromSeconds 15.) (fun () ->
            task {
                use conn = new NpgsqlConnection(fx.DbConnectionString)
                let! id =
                    conn.QuerySingleAsync<int64>(
                        "SELECT COALESCE((SELECT id FROM pending_add_batch WHERE user_id=@u ORDER BY id DESC LIMIT 1), 0)",
                        {| u = ownerId |})
                return if id = 0L then None else Some id
            })

    let waitForAllItemsTerminal (batchId: int64) =
        waitFor "batch items to leave 'pending'" (TimeSpan.FromSeconds 60.) (fun () ->
            task {
                use conn = new NpgsqlConnection(fx.DbConnectionString)
                let! pendingCount =
                    conn.QuerySingleAsync<int64>(
                        "SELECT COUNT(*)::bigint FROM pending_add_batch_item WHERE batch_id=@b AND status='pending'",
                        {| b = batchId |})
                return if pendingCount = 0L then Some () else None
            })

    let waitForBatchStatus (batchId: int64) (expected: string) =
        waitFor $"batch {batchId} status='{expected}'" (TimeSpan.FromSeconds 30.) (fun () ->
            task {
                use conn = new NpgsqlConnection(fx.DbConnectionString)
                let! status =
                    conn.QuerySingleAsync<string>(
                        "SELECT COALESCE((SELECT status FROM pending_add_batch WHERE id=@b), '__GONE__')",
                        {| b = batchId |})
                return if status = expected then Some () else None
            })

    let waitForBatchCleared (batchId: int64) =
        waitFor $"batch {batchId} cleared" (TimeSpan.FromSeconds 30.) (fun () ->
            task {
                use conn = new NpgsqlConnection(fx.DbConnectionString)
                let! count = conn.QuerySingleAsync<int64>("SELECT COUNT(*)::bigint FROM pending_add_batch WHERE id=@b", {| b = batchId |})
                return if count = 0L then Some () else None
            })

    let countCouponsForOwner (ownerId: int64) =
        task {
            use conn = new NpgsqlConnection(fx.DbConnectionString)
            return! conn.QuerySingleAsync<int64>("SELECT COUNT(*)::bigint FROM coupon WHERE owner_id=@u", {| u = ownerId |})
        }

    let latestCouponForOwner (ownerId: int64) =
        task {
            use conn = new NpgsqlConnection(fx.DbConnectionString)
            return!
                conn.QuerySingleAsync<WizardCouponRow>(
                    "SELECT id, value, min_check FROM coupon WHERE owner_id = @owner_id ORDER BY id DESC LIMIT 1",
                    {| owner_id = ownerId |})
        }

    /// No DbSeed.fs helper exists for this (contract: "add it as a private function
    /// inside your own file" when a shared helper is missing) — overwrites the PENDING
    /// WIZARD ROW's own `expires_at` (table `pending_add`, column `expires_at` — schema
    /// confirmed against V6__recreate_pending_add.sql / V7__pending_add_barcode.sql, NOT
    /// guessed), the same table DbSeed.deletePendingAddFlowAsync already targets. This is
    /// what makes the `addflow:ocr:yes` test below (Fix 4) reachable — see that test's
    /// doc comment for the full "why" and the public-repo constraint that makes this SQL
    /// bump necessary at all.
    let bumpPendingAddExpiryAsync (userId: int64) (expiresAt: string) : Task =
        task {
            use conn = new NpgsqlConnection(fx.DbConnectionString)
            let! _ =
                conn.ExecuteAsync(
                    "UPDATE pending_add SET expires_at = @expires_at::date WHERE user_id = @user_id",
                    {| user_id = userId; expires_at = expiresAt |})
            ()
        }

    /// Drives a fresh wizard from a bare "/add" through a bare (caption-less) photo of
    /// `fixtureImage`, through the OCR-confirm screen's `addflow:ocr:no`, and returns
    /// the ensuing "Выбери скидку и минимальный чек" screen (with the four
    /// `addflow:disc:*` buttons — BotHelpers.fs:253-259). See class doc comment for why
    /// `addflow:ocr:no` is always reachable and always the right way past the
    /// OCR-derived (expired) date.
    let reachDiscountChoice (fixtureImage: string) : Task<TL.Message> =
        task {
            let imagePath = Path.Combine(RealEnv.ocrFixtureImagesDir, fixtureImage)
            Assert.True(File.Exists imagePath, $"Expired-coupon fixture missing: {imagePath}")

            let! addSentId = fx.UserClient.SendText(fx.BotChatId, "/add")
            let! _askPhoto = fx.UserClient.AwaitTextContaining(fx.BotChatId, addSentId, "Пришли фото", TimeSpan.FromSeconds 60.)

            let! photoSentId = fx.UserClient.SendPhoto(fx.BotChatId, imagePath, "")

            // Real Azure OCR call happens synchronously inside HandleAddWizardPhoto —
            // same 90s budget AddFlowRealTests.fs gives its own OCR+add round trip.
            let! _ocrConfirmMsg =
                fx.UserClient.AwaitAndPressButton(
                    fx.BotChatId,
                    photoSentId,
                    "Всё верно?",
                    TimeSpan.FromSeconds 90.,
                    (fun d -> d = "addflow:ocr:no"),
                    "addflow:ocr:no")

            return! fx.UserClient.AwaitTextContaining(fx.BotChatId, photoSentId, "выбери скидку", TimeSpan.FromSeconds 60.)
        }

    /// Presses `discountData` (e.g. "addflow:disc:10:50") on `discountMsg`, then
    /// `addflow:date:today` on the ensuing date-choice screen (always reached — see
    /// class doc comment), and returns the resulting confirm screen.
    let pickDiscountReachConfirm (discountMsg: TL.Message) (discountData: string) : Task<TL.Message> =
        task {
            do! fx.UserClient.PressCallbackButtonMatching(fx.BotChatId, discountMsg, (fun d -> d = discountData), discountData)
            let! dateMsg =
                fx.UserClient.AwaitAndPressButton(
                    fx.BotChatId,
                    discountMsg.id,
                    "Выбери дату истечения",
                    TimeSpan.FromSeconds 60.,
                    (fun d -> d = "addflow:date:today"),
                    "addflow:date:today")
            return! fx.UserClient.AwaitTextContaining(fx.BotChatId, dateMsg.id, "Подтвердить добавление купона", TimeSpan.FromSeconds 60.)
        }

    /// Test 1 (contract item 1): full wizard happy path. bare "/add" -> photo ->
    /// addflow:ocr:no -> addflow:disc:10:50 -> addflow:date:today -> addflow:confirm.
    /// Asserts "Добавлен купон ID:" and that the coupon lands in the DB with the
    /// chosen value/min_check.
    [<Fact>]
    member _.``full wizard happy path: ocr:no, disc:10:50, date:today, confirm adds a coupon``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let ownerId = fx.UserClient.Me.id
            do! DbSeed.deletePendingAddFlowAsync fx.DbConnectionString ownerId
            do! DbSeed.deletePendingBatchesAsync fx.DbConnectionString ownerId

            let fixtureImage = "10_50_01-19_01-28_2706613152454.jpg"
            let barcode = RealTestHelpers.fixtureBarcodes.[fixtureImage]
            do! DbSeed.deleteCouponsByBarcodeAsync fx.DbConnectionString barcode

            let! discountMsg = reachDiscountChoice fixtureImage
            let! confirmMsg = pickDiscountReachConfirm discountMsg "addflow:disc:10:50"
            Assert.Contains("10€ из 50€", confirmMsg.message)

            do! fx.UserClient.PressCallbackButtonMatching(fx.BotChatId, confirmMsg, (fun d -> d = "addflow:confirm"), "addflow:confirm")
            let! addedMsg = fx.UserClient.AwaitTextContaining(fx.BotChatId, confirmMsg.id, "Добавлен купон ID:", TimeSpan.FromSeconds 60.)

            let! coupon = latestCouponForOwner ownerId
            Assert.Equal(10m, coupon.value)
            Assert.Equal(50m, coupon.min_check)
            Assert.Contains($"ID:{coupon.id}", addedMsg.message)
        })

    /// Test 2 (contract item 2): addflow:date:today vs addflow:date:tomorrow.
    ///
    /// Rewritten (review Fix 3 + Fix 1): the original version asserted
    /// `formatUiDate (DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1.0)))` against the
    /// confirm screen — a date computed from the TEST PROCESS's own clock, which
    /// disagrees with the bot's frozen-and-advanceable `FakeTimeProvider` notion of
    /// "today" whenever the pod booted on the other side of a UTC midnight from this
    /// process, or after any earlier test in the run advanced the clock. It also never
    /// pressed `addflow:confirm` or `addflow:cancel`, leaving a pending `pending_add`
    /// row behind for a different, unrelated test to sweep — an ordering dependency in a
    /// suite with no guaranteed test order.
    ///
    /// This version drives TWO full wizard flows to completion (through
    /// `addflow:confirm`, one per date button, distinct fixtures so their barcodes never
    /// collide) and compares the resulting `coupon.expires_at` DB rows directly to each
    /// other. That proves the RELATIONSHIP ("tomorrow" is exactly one day after "today",
    /// and the two are different) entirely from the bot's own recorded state — no
    /// reference to this test process's `DateTime.UtcNow` anywhere in the assertion —
    /// and each sub-flow reaches `addflow:confirm`, so nothing is left pending afterward.
    [<Fact>]
    member _.``date:today and date:tomorrow set different, one-day-apart expiries``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let ownerId = fx.UserClient.Me.id

            let addViaDateButtonAndGetExpiry (fixtureImage: string) (dateButtonData: string) : Task<DateOnly> =
                task {
                    do! DbSeed.deletePendingAddFlowAsync fx.DbConnectionString ownerId
                    do! DbSeed.deletePendingBatchesAsync fx.DbConnectionString ownerId

                    let barcode = RealTestHelpers.fixtureBarcodes.[fixtureImage]
                    do! DbSeed.deleteCouponsByBarcodeAsync fx.DbConnectionString barcode

                    let! discountMsg = reachDiscountChoice fixtureImage
                    do!
                        fx.UserClient.PressCallbackButtonMatching(
                            fx.BotChatId, discountMsg, (fun d -> d = "addflow:disc:10:50"), "addflow:disc:10:50")
                    let! dateMsg = fx.UserClient.AwaitTextContaining(fx.BotChatId, discountMsg.id, "Выбери дату истечения", TimeSpan.FromSeconds 60.)

                    do! fx.UserClient.PressCallbackButtonMatching(fx.BotChatId, dateMsg, (fun d -> d = dateButtonData), dateButtonData)
                    let! confirmMsg = fx.UserClient.AwaitTextContaining(fx.BotChatId, dateMsg.id, "Подтвердить добавление купона", TimeSpan.FromSeconds 60.)

                    do! fx.UserClient.PressCallbackButtonMatching(fx.BotChatId, confirmMsg, (fun d -> d = "addflow:confirm"), "addflow:confirm")
                    let! _addedMsg = fx.UserClient.AwaitTextContaining(fx.BotChatId, confirmMsg.id, "Добавлен купон ID:", TimeSpan.FromSeconds 60.)

                    use conn = new NpgsqlConnection(fx.DbConnectionString)
                    return!
                        conn.QuerySingleAsync<DateOnly>(
                            "SELECT expires_at FROM coupon WHERE owner_id = @owner_id ORDER BY id DESC LIMIT 1",
                            {| owner_id = ownerId |})
                }

            let! todayExpiry = addViaDateButtonAndGetExpiry "10_50_01-12_01-21_2706513420233.jpg" "addflow:date:today"
            let! tomorrowExpiry = addViaDateButtonAndGetExpiry "10_50_01-12_01-21_2706530490622.jpg" "addflow:date:tomorrow"

            Assert.Equal(todayExpiry.AddDays(1), tomorrowExpiry)
            Assert.NotEqual(todayExpiry, tomorrowExpiry)
        })

    /// Test 3 (contract item 3): addflow:cancel. Reaches the confirm screen, cancels,
    /// asserts the cancellation text and that no coupon was created for this fixture's
    /// barcode.
    [<Fact>]
    member _.``addflow:cancel aborts without creating a coupon``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let ownerId = fx.UserClient.Me.id
            do! DbSeed.deletePendingAddFlowAsync fx.DbConnectionString ownerId
            do! DbSeed.deletePendingBatchesAsync fx.DbConnectionString ownerId

            let fixtureImage = "10_50_01-14_01-23_2706658654210.jpg"
            let barcode = RealTestHelpers.fixtureBarcodes.[fixtureImage]
            do! DbSeed.deleteCouponsByBarcodeAsync fx.DbConnectionString barcode

            let! discountMsg = reachDiscountChoice fixtureImage
            let! confirmMsg = pickDiscountReachConfirm discountMsg "addflow:disc:10:50"

            do! fx.UserClient.PressCallbackButtonMatching(fx.BotChatId, confirmMsg, (fun d -> d = "addflow:cancel"), "addflow:cancel")
            let! _cancelReply = fx.UserClient.AwaitTextContaining(fx.BotChatId, confirmMsg.id, "Ок, добавление купона отменено.", TimeSpan.FromSeconds 60.)

            use conn = new NpgsqlConnection(fx.DbConnectionString)
            let! count = conn.QuerySingleAsync<int64>("SELECT COUNT(*)::bigint FROM coupon WHERE barcode_text = @b", {| b = barcode |})
            Assert.Equal(0L, count)
        })

    /// Test 4 (contract item 4): the three addflow:disc:* buttons NOT exercised by test
    /// 1 (which used "addflow:disc:10:50" — BotHelpers.addWizardDiscountKeyboard,
    /// BotHelpers.fs:253-259). Kept cheap per the brief: no addflow:confirm / DB insert
    /// in any iteration — each iteration reaches the confirm screen (the only screen
    /// that echoes the chosen value/min_check in text — see class doc comment on why
    /// the intermediate date-choice screen does not) and cancels. Reuses the same
    /// fixture across all three iterations: since no TryAddCoupon call ever happens
    /// here, there is no barcode-uniqueness concern to route around.
    [<Fact>]
    member _.``remaining discount buttons reflect the chosen value/min-check on the confirm screen``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let ownerId = fx.UserClient.Me.id
            let fixtureImage = "5_25_01-15_01-21_2706666377231.jpg"

            let remaining =
                [ "addflow:disc:5:25", "5€ из 25€"
                  "addflow:disc:10:40", "10€ из 40€"
                  "addflow:disc:20:100", "20€ из 100€" ]

            for discountData, expectedValueText in remaining do
                do! DbSeed.deletePendingAddFlowAsync fx.DbConnectionString ownerId
                do! DbSeed.deletePendingBatchesAsync fx.DbConnectionString ownerId

                let! discountMsg = reachDiscountChoice fixtureImage
                let! confirmMsg = pickDiscountReachConfirm discountMsg discountData
                Assert.Contains(expectedValueText, confirmMsg.message)

                do! fx.UserClient.PressCallbackButtonMatching(fx.BotChatId, confirmMsg, (fun d -> d = "addflow:cancel"), "addflow:cancel")
                let! _cancelReply = fx.UserClient.AwaitTextContaining(fx.BotChatId, confirmMsg.id, "Ок, добавление купона отменено.", TimeSpan.FromSeconds 60.)
                ()
        })

    /// Test 5 (Fix 4, review follow-up): addflow:ocr:yes. Previously untestable as a
    /// happy path — see class doc comment — because `addflow:ocr:yes`
    /// (CallbackHandler.fs:258-293) calls `TryAddCoupon` with the wizard's OWN staged
    /// `expires_at`, which for a bare (caption-less) photo of any fixture in this public
    /// repo is OCR's own PRINTED date, always in the past (repo-public constraint: a
    /// fixture with a future printed date would be a currently-usable, live coupon
    /// barcode). `DbService.TryAddCoupon` rejects `expires_at < todayUtc()` unconditionally
    /// (DbService.fs:198-200) before any DB write.
    ///
    /// WHY THE SQL BUMP BELOW EXISTS — READ BEFORE "CLEANING THIS UP": fixture photos
    /// must stay expired forever (public-repo constraint above), so the printed date OCR
    /// reads off them can never be future. `bumpPendingAddExpiryAsync` overwrites only
    /// the PENDING WIZARD ROW's staged `pending_add.expires_at` (the value
    /// `addflow:ocr:yes` will hand to `TryAddCoupon`), not the fixture photo itself — the
    /// same "override the DB row that gates TryAddCoupon, never the photo"
    /// trick BulkAddRealTests.fs's `bumpItemsToFutureExpiry` already uses for the album
    /// path's `pending_add_batch_item.expires_at`. Without this bump, `addflow:ocr:yes`
    /// can only ever be exercised against the "Нельзя добавить истёкший купон" rejection
    /// branch — this bump is the only way to reach `AddCouponResult.Added` through this
    /// specific button without publishing a live barcode. Removing it "as unnecessary
    /// scaffolding" would silently regress this test back to only covering rejection.
    [<Fact>]
    member _.``addflow:ocr:yes adds a coupon using OCR's own value/min-check once the staged expiry is future``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let ownerId = fx.UserClient.Me.id
            do! DbSeed.deletePendingAddFlowAsync fx.DbConnectionString ownerId
            do! DbSeed.deletePendingBatchesAsync fx.DbConnectionString ownerId

            let fixtureImage = "10_50_01-04_01-13_2706602781191.jpg"
            let barcode = RealTestHelpers.fixtureBarcodes.[fixtureImage]
            do! DbSeed.deleteCouponsByBarcodeAsync fx.DbConnectionString barcode

            let imagePath = Path.Combine(RealEnv.ocrFixtureImagesDir, fixtureImage)
            Assert.True(File.Exists imagePath, $"Expired-coupon fixture missing: {imagePath}")

            let! addSentId = fx.UserClient.SendText(fx.BotChatId, "/add")
            let! _askPhoto = fx.UserClient.AwaitTextContaining(fx.BotChatId, addSentId, "Пришли фото", TimeSpan.FromSeconds 60.)

            let! photoSentId = fx.UserClient.SendPhoto(fx.BotChatId, imagePath, "")
            // Real Azure OCR call happens synchronously inside HandleAddWizardPhoto —
            // same 90s budget reachDiscountChoice/AddFlowRealTests.fs give this round trip.
            let! ocrConfirmMsg =
                fx.UserClient.AwaitTextContaining(fx.BotChatId, photoSentId, "Всё верно?", TimeSpan.FromSeconds 90.)

            // Bump the staged wizard row's expiry PAST TryAddCoupon's expiry gate — see
            // this test's doc comment for why this (and never touching the fixture
            // photo itself) is the correct and only way to reach the Added branch.
            let futureDate = RealTestHelpers.futureExpiry 365.
            do! bumpPendingAddExpiryAsync ownerId futureDate

            do! fx.UserClient.PressCallbackButtonMatching(fx.BotChatId, ocrConfirmMsg, (fun d -> d = "addflow:ocr:yes"), "addflow:ocr:yes")
            let! addedMsg = fx.UserClient.AwaitTextContaining(fx.BotChatId, ocrConfirmMsg.id, "Добавлен купон ID:", TimeSpan.FromSeconds 60.)

            let! coupon = latestCouponForOwner ownerId
            // OCR-derived value/min_check for this fixture — reliably 10/50, per the
            // filename convention and OcrTests.fs's own theory case for this exact file
            // (class doc comment: all 16 "good" fixtures OCR value+min_check+valid_to
            // reliably).
            Assert.Equal(10m, coupon.value)
            Assert.Equal(50m, coupon.min_check)
            Assert.Contains($"ID:{coupon.id}", addedMsg.message)

            use conn = new NpgsqlConnection(fx.DbConnectionString)
            let! couponRow =
                conn.QuerySingleAsync<CouponRow>(
                    "SELECT id, value, min_check, expires_at, barcode_text FROM coupon WHERE id = @coupon_id",
                    {| coupon_id = coupon.id |})
            Assert.Equal(DateOnly.ParseExact(futureDate, "yyyy-MM-dd", CultureInfo.InvariantCulture), couponRow.expires_at)
            Assert.Equal(barcode, couponRow.barcode_text)
        })

    /// Test 6 (contract item 6): addflow:bulk:cancel:<batchId>. Album mechanics copied
    /// from BulkAddRealTests.fs's confirm test, minus its `bumpItemsToFutureExpiry`
    /// step — BulkBatchCancel (CallbackHandler.fs:40-52) only calls db.ClearBatch and
    /// edits the message, it never calls TryAddCoupon, so there is no expiry gate to
    /// route around for a cancel-only test. The "↩️ Отменить" button
    /// (`addflow:bulk:cancel:<batchId>`) is present on the bulk-confirm keyboard
    /// regardless of how many items OCR'd successfully (BotHelpers.addBatchConfirmKeyboard,
    /// BotHelpers.fs:279-287), so this doesn't depend on OCR outcome the way
    /// BulkAddRealTests' confirm test does.
    [<Fact>]
    member _.``addflow:bulk:cancel cancels the album without adding coupons``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let ownerId = fx.UserClient.Me.id
            do! DbSeed.deletePendingBatchesAsync fx.DbConnectionString ownerId
            do! DbSeed.deletePendingAddFlowAsync fx.DbConnectionString ownerId

            let images =
                [ "10_50_2026-01-17_2026-01-26_2706688198821.jpg"
                  "10_50_2026-01-17_2026-01-26_2706688198838.jpg" ]
                |> List.map (fun f -> Path.Combine(RealEnv.ocrFixtureImagesDir, f))
            images |> List.iter (fun p -> Assert.True(File.Exists p, $"Expired-coupon fixture missing: {p}"))

            let! countBefore = countCouponsForOwner ownerId

            let! lastAlbumMsgId = fx.UserClient.SendAlbum(fx.BotChatId, images)

            let! batchId = waitForBatchByOwner ownerId
            do! waitForAllItemsTerminal batchId
            do! fx.AdvanceClockAsync 2000 // BATCH_DEBOUNCE_MS=1000 (contract seed) + margin
            do! waitForBatchStatus batchId "awaiting_user"

            let! confirmMsg = fx.UserClient.AwaitTextContaining(fx.BotChatId, lastAlbumMsgId, "Подтвердить", TimeSpan.FromSeconds 30.)
            do!
                fx.UserClient.PressCallbackButtonMatching(
                    fx.BotChatId,
                    confirmMsg,
                    (fun d -> d.StartsWith "addflow:bulk:cancel:"),
                    "addflow:bulk:cancel:<batchId>")

            let! _cancelled = fx.UserClient.AwaitTextContaining(fx.BotChatId, confirmMsg.id, "Ок, пакет отменён.", TimeSpan.FromSeconds 30.)
            do! waitForBatchCleared batchId

            let! countAfter = countCouponsForOwner ownerId
            Assert.Equal(countBefore, countAfter)
        })
