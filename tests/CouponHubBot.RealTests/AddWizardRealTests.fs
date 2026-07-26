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
/// ── `addflow:ocr:yes` is UNREACHABLE as a happy path for these fixtures, left
/// untested (see individual test docs below for the full citation) ──
/// `addflow:ocr:yes` (CallbackHandler.fs:258-293) calls `TryAddCoupon` directly with
/// `flow.expires_at.Value` — the OCR-derived date, which for every fixture in this
/// public repo is deliberately in the past (repo-public constraint, see
/// AddFlowRealTests.fs's doc comment). `DbService.TryAddCoupon` rejects
/// `expires_at < todayUtc()` unconditionally (DbService.fs:198-200) BEFORE any DB
/// write, so `addflow:ocr:yes` against any of these fixtures can only ever reach the
/// "Нельзя добавить истёкший купон" rejection branch (CallbackHandler.fs:285-287) —
/// never `AddCouponResult.Added`. There is no photo in this fixture set whose printed
/// date is in the future (every fixture is intentionally expired), so no happy path
/// through `addflow:ocr:yes` can be constructed without either (a) publishing a
/// currently-usable coupon photo (forbidden) or (b) overwriting `pending_add.expires_at`
/// via direct SQL before pressing "yes" — which would stop exercising what a real user
/// pressing "yes" on a real OCR-recognized (expired) photo actually experiences, i.e.
/// it would silently paper over the rejection this task's brief explicitly says not to
/// hide. Left untested per that instruction.
type AddWizardRealTests(fx: RealAssemblyFixture) =

    let ru = CultureInfo("ru-RU")

    /// Mirrors Utils.DateFormatting.formatDateNoYearWithDow (Utils.fs:36-37) — the
    /// exact rendering `BotHelpers.formatUiDate` uses for every date shown in wizard
    /// screens, so test-side expectations can be built the same way the bot itself
    /// builds its reply text.
    let formatUiDate (d: DateOnly) = d.ToString("d MMMM, dddd", ru)

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

    /// Test 2 (contract item 2): addflow:date:tomorrow. Same chain as test 1 up to the
    /// date choice, but presses "tomorrow" and asserts the confirm screen reflects
    /// tomorrow's date — does NOT press addflow:confirm (no DB assertion needed per the
    /// brief; the wizard is left pending and gets swept by the next test's
    /// deletePendingAddFlowAsync).
    [<Fact>]
    member _.``date:tomorrow reflects tomorrow's date on the confirm screen``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let ownerId = fx.UserClient.Me.id
            do! DbSeed.deletePendingAddFlowAsync fx.DbConnectionString ownerId
            do! DbSeed.deletePendingBatchesAsync fx.DbConnectionString ownerId

            let! discountMsg = reachDiscountChoice "10_50_01-21_01-30_2706616470579.jpg"
            do! fx.UserClient.PressCallbackButtonMatching(fx.BotChatId, discountMsg, (fun d -> d = "addflow:disc:10:50"), "addflow:disc:10:50")
            let! dateMsg = fx.UserClient.AwaitTextContaining(fx.BotChatId, discountMsg.id, "Выбери дату истечения", TimeSpan.FromSeconds 60.)

            do! fx.UserClient.PressCallbackButtonMatching(fx.BotChatId, dateMsg, (fun d -> d = "addflow:date:tomorrow"), "addflow:date:tomorrow")
            let! confirmMsg = fx.UserClient.AwaitTextContaining(fx.BotChatId, dateMsg.id, "Подтвердить добавление купона", TimeSpan.FromSeconds 60.)

            // The wizard's "tomorrow" is computed from the bot pod's own (frozen at
            // boot, unless BOT_FIXED_UTC_NOW is set — Program.fs:111-122) TimeProvider,
            // not this test process's clock; a midnight-UTC-boundary mismatch between
            // the two is a known, accepted flake risk for this specific assertion (see
            // final report).
            let tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1.0))
            Assert.Contains(formatUiDate tomorrow, confirmMsg.message)
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
