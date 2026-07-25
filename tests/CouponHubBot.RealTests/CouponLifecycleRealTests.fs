namespace CouponHubBot.RealTests

open System
open System.Globalization
open System.IO
open System.Threading.Tasks
open Dapper
open Npgsql
open Xunit

/// Coverage items 4-7 (contract):
///   4. /list shows the coupon; take:<id> callback takes it.
///   5. /my shows it with both buttons and the adder handle.
///   6. used:<id> marks used; /stats reflects it.
///   7. return:<id> returns to pool.
///
/// This suite drives exactly ONE real Telegram account (see MembershipGateRealTests'
/// doc comment), so "owner adds, someone else takes" collapses to the same account
/// adding AND taking its own coupon — DbService.TryTakeCoupon has no self-take
/// restriction (checked: DbService.fs:473-547), so this is a faithful exercise of the
/// take/used/return state machine, just not of a *second person's* perspective.
///
/// Each test adds its OWN coupon (own expired-image fixture, own barcode) rather than
/// chaining off a previous test's state — xUnit does not guarantee cross-[<Fact>]
/// ordering, and take/used/return are mutually exclusive terminal-ish states for a
/// single coupon.
type CouponLifecycleRealTests(fx: RealAssemblyFixture) =

    let futureExpiry = DateTime.UtcNow.AddDays(200.).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

    let latestCouponIdForOwner (ownerId: int64) =
        task {
            use conn = new NpgsqlConnection(fx.DbConnectionString)
            return! conn.QuerySingleAsync<int>("SELECT id FROM coupon WHERE owner_id=@o ORDER BY id DESC LIMIT 1", {| o = ownerId |})
        }

    /// Adds a coupon via photo + explicit "/add <value> <minCheck> <futureDate>"
    /// caption (same reasoning as AddFlowRealTests: OCR still runs server-side, but the
    /// caption's explicit values are what the app actually uses — see AddFlowRealTests'
    /// doc comment for why a bare "/add" + OCR-derived expiry can't be used against an
    /// intentionally-expired fixture image). Returns the new coupon's id.
    let addCoupon (fixtureFileName: string) (value: string) (minCheck: string) =
        task {
            let imagePath = Path.Combine(RealEnv.ocrFixtureImagesDir, fixtureFileName)
            Assert.True(File.Exists imagePath, $"Expired-coupon fixture missing: {imagePath}")

            let! sentId = fx.UserClient.SendPhoto(fx.BotChatId, imagePath, $"/add {value} {minCheck} {futureExpiry}")
            let! _reply = fx.UserClient.AwaitTextContaining(fx.BotChatId, sentId, "Добавлен купон", TimeSpan.FromSeconds 90.)
            return! latestCouponIdForOwner fx.UserClient.Me.id
        }

    [<Fact>]
    member _.``list shows the coupon and its take callback takes it``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let! couponId = addCoupon "10_50_01-04_01-13_2706602781191.jpg" "10" "50"

            let! sentId = fx.UserClient.SendText(fx.BotChatId, "/list")
            let! listMsg = fx.UserClient.AwaitTextContaining(fx.BotChatId, sentId, $"ID:{couponId}", TimeSpan.FromSeconds 60.)

            let takeData = fx.UserClient.FindCallbackData(listMsg, fun d -> d = $"take:{couponId}")
            Assert.True(takeData.IsSome, $"Expected a take:{couponId} button on the /list message")

            do! fx.UserClient.PressCallbackButton(fx.BotChatId, listMsg.id, takeData.Value)
            let! takenMsg =
                fx.UserClient.AwaitPhotoCaptionContaining(fx.BotChatId, listMsg.id, $"Купон ID:{couponId} теперь твой", TimeSpan.FromSeconds 60.)
            Assert.Contains("теперь твой", takenMsg.message)
        })

    [<Fact>]
    member _.``my shows the taken coupon with return/used buttons and the adder handle``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let! couponId = addCoupon "10_50_01-06_01-15_2706643333717.jpg" "12" "60"
            let! takeSentId = fx.UserClient.SendText(fx.BotChatId, $"/take {couponId}")
            let! _taken = fx.UserClient.AwaitPhotoCaptionContaining(fx.BotChatId, takeSentId, "теперь твой", TimeSpan.FromSeconds 60.)

            let! mySentId = fx.UserClient.SendText(fx.BotChatId, "/my")
            let! myMsg = fx.UserClient.AwaitTextContaining(fx.BotChatId, mySentId, $"Купон ID:{couponId}", TimeSpan.FromSeconds 60.)

            // Same account both adds and takes here (see class doc comment) — the adder
            // handle rendered by BotHelpers.formatUserHandle is therefore this account's
            // own "@username" (or first name / numeric id, in that fallback order).
            let me = fx.UserClient.Me
            let expectedHandle =
                if not (String.IsNullOrWhiteSpace me.username) then "@" + me.username
                elif not (String.IsNullOrWhiteSpace me.first_name) then me.first_name
                else string me.id
            Assert.Contains("добавил", myMsg.message)
            Assert.Contains(expectedHandle, myMsg.message)

            // /my's per-coupon listing keyboard (CommandHandler.fs's InlineKeyboardButton.Create
            // calls, distinct from singleTakenKeyboard below) emits bare "return:<id>"/"used:<id>"
            // with no suffix — unlike the take-confirmation card's buttons, see the two tests below.
            let hasReturn = fx.UserClient.FindCallbackData(myMsg, fun d -> d = $"return:{couponId}")
            let hasUsed = fx.UserClient.FindCallbackData(myMsg, fun d -> d = $"used:{couponId}")
            Assert.True(hasReturn.IsSome, $"Expected a return:{couponId} button under /my")
            Assert.True(hasUsed.IsSome, $"Expected a used:{couponId} button under /my")
        })

    [<Fact>]
    member _.``used callback marks the coupon used and stats reflects it``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let! couponId = addCoupon "10_50_01-12_01-21_2706513420233.jpg" "10" "50"
            let! takeSentId = fx.UserClient.SendText(fx.BotChatId, $"/take {couponId}")
            let! takenMsg = fx.UserClient.AwaitPhotoCaptionContaining(fx.BotChatId, takeSentId, "теперь твой", TimeSpan.FromSeconds 60.)

            // BotHelpers.singleTakenKeyboard (used by handleTake's take-confirmation card,
            // src/CouponHubBot/Services/BotHelpers.fs:290-294) emits "used:<id>:del" — a
            // ":del" suffix the /my listing's separate keyboard does NOT have (see the test
            // above). An exact match on the bare "used:<id>" (no suffix) can never find this
            // button; confirmed against the real bot's reply in run 30163952072 (2026-07-25).
            let usedData = fx.UserClient.FindCallbackData(takenMsg, fun d -> d = $"used:{couponId}:del")
            Assert.True(usedData.IsSome, $"Expected a used:{couponId}:del button on the take confirmation")

            do! fx.UserClient.PressCallbackButton(fx.BotChatId, takenMsg.id, usedData.Value)
            let! _usedReply = fx.UserClient.AwaitTextContaining(fx.BotChatId, takenMsg.id, $"Купон ID:{couponId} отмечен", TimeSpan.FromSeconds 60.)

            use conn = new NpgsqlConnection(fx.DbConnectionString)
            let! status = conn.QuerySingleAsync<string>("SELECT status FROM coupon WHERE id=@id", {| id = couponId |})
            Assert.Equal("used", status)

            let! statsSentId = fx.UserClient.SendText(fx.BotChatId, "/stats")
            let! statsReply = fx.UserClient.AwaitTextContaining(fx.BotChatId, statsSentId, "Отмечено использованными", TimeSpan.FromSeconds 60.)
            Assert.Contains("Отмечено использованными", statsReply.message)
        })

    [<Fact>]
    member _.``return callback returns the coupon to the pool``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let! couponId = addCoupon "10_50_01-12_01-21_2706530490622.jpg" "10" "50"
            let! takeSentId = fx.UserClient.SendText(fx.BotChatId, $"/take {couponId}")
            let! takenMsg = fx.UserClient.AwaitPhotoCaptionContaining(fx.BotChatId, takeSentId, "теперь твой", TimeSpan.FromSeconds 60.)

            // Same ":del"-suffixed singleTakenKeyboard format as the "used" test above.
            let returnData = fx.UserClient.FindCallbackData(takenMsg, fun d -> d = $"return:{couponId}:del")
            Assert.True(returnData.IsSome, $"Expected a return:{couponId}:del button on the take confirmation")

            do! fx.UserClient.PressCallbackButton(fx.BotChatId, takenMsg.id, returnData.Value)
            let! _returnedReply = fx.UserClient.AwaitTextContaining(fx.BotChatId, takenMsg.id, $"Купон ID:{couponId} возвращён", TimeSpan.FromSeconds 60.)

            use conn = new NpgsqlConnection(fx.DbConnectionString)
            let! status = conn.QuerySingleAsync<string>("SELECT status FROM coupon WHERE id=@id", {| id = couponId |})
            Assert.Equal("available", status)
        })
