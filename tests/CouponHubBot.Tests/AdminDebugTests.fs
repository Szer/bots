namespace CouponHubBot.Tests

open BotTestInfra
open System.Net
open Dapper
open Npgsql
open Xunit
open FakeCallHelpers

type AdminDebugTests(fixture: DefaultCouponHubTestContainers) =

    // Admin user ID 900 is configured in FEEDBACK_ADMINS for the test container.
    let adminId = 900L

    let getLatestCouponId () =
        task {
            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            //language=postgresql
            let sql = "SELECT id FROM coupon ORDER BY id DESC LIMIT 1"
            return! conn.QuerySingleAsync<int>(sql)
        }

    // The admin-facing /debug reply: details header + <pre> history block, one message.
    let findDebugReply (calls: FakeCall array) =
        calls |> Array.tryPick (fun call ->
            match parseCallBody call.Body with
            | Some parsed when parsed.ChatId = Some adminId && parsed.Text.IsSome && parsed.Text.Value.Contains("<pre>") ->
                Some parsed.Text.Value
            | _ -> None)

    [<Fact>]
    let ``Non-admin user sends debug command and gets no response`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let user = Tg.user(id = 800L, username = "regular_user", firstName = "Regular")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let! resp = fixture.SendUpdate(Tg.dmMessage("/debug 1", user))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.Equal(0, calls.Length)
        }

    [<Fact>]
    let ``Admin user sends debug command and gets formatted event history`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            // Admin user (900 is in FEEDBACK_ADMINS)
            let admin = Tg.user(id = adminId, username = "admin_debug", firstName = "Admin")
            do! fixture.SetChatMemberStatus(admin.Id, "member")

            // Add a coupon to generate an 'added' event
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", admin))
            let! couponId = getLatestCouponId ()

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage($"/debug {couponId}", admin))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            let debugResponse = findDebugReply calls
            Assert.True(debugResponse.IsSome, "Admin should receive debug output with <pre> block")

            let text = debugResponse.Value
            Assert.Contains("date", text)
            Assert.Contains("user", text)
            Assert.Contains("event_type", text)
            Assert.Contains("added", text)
            Assert.Contains("admin_debug", text)
        }

    [<Fact>]
    let ``Debug shows every coupon field: value, dates, barcode, status, owner and holder`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            let admin = Tg.user(id = adminId, username = "admin_debug", firstName = "Admin")
            let owner = Tg.user(id = 810L, username = "debug_owner", firstName = "Owner")
            let taker = Tg.user(id = 811L, username = "debug_taker", firstName = "Taker")
            do! fixture.SetChatMemberStatus(admin.Id, "member")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()

            // OCR is off in this fixture, so barcode/valid_from are set directly — /debug must
            // report whatever is stored, no matter which path filled it in.
            let! _ =
                fixture.Execute(
                    //language=postgresql
                    "UPDATE coupon SET barcode_text = @barcode, valid_from = @validFrom::date WHERE id = @id",
                    {| barcode = "9501234567890"; validFrom = "2025-12-20"; id = couponId |})

            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage($"/debug {couponId}", admin))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            let debugResponse = findDebugReply calls
            Assert.True(debugResponse.IsSome, "Admin should receive debug output with <pre> block")
            let text = debugResponse.Value

            Assert.Contains($"Купон ID:{couponId}", text)
            Assert.Contains("Номинал: 10€ из 50€", text)
            Assert.Contains("Действует с: 2025-12-20", text)
            Assert.Contains("Годен до: 2026-01-25", text)
            Assert.Contains("Штрихкод: 9501234567890", text)
            Assert.Contains("Статус: taken", text)
            Assert.Contains("Владелец: @debug_owner (id:810)", text)
            Assert.Contains("Взят: @debug_taker (id:811)", text)
            Assert.Contains("Добавлен:", text)
            // …and the event history is still there, below the details.
            Assert.Contains("event_type", text)
            Assert.Contains("taken", text)

            // /debug is text-only: the admin gets no coupon photo.
            let! photoCalls = fixture.GetFakeCalls("sendPhoto")
            let photoToAdmin =
                photoCalls |> Array.exists (fun call ->
                    match parseCallBody call.Body with
                    | Some parsed -> parsed.ChatId = Some adminId
                    | None -> false)
            Assert.False(photoToAdmin, "/debug must not send a photo")
        }

    [<Fact>]
    let ``Debug renders missing barcode, missing valid_from and untaken state as placeholders`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            let admin = Tg.user(id = adminId, username = "admin_debug", firstName = "Admin")
            let owner = Tg.user(id = 812L, username = "plain_owner", firstName = "Owner")
            do! fixture.SetChatMemberStatus(admin.Id, "member")
            do! fixture.SetChatMemberStatus(owner.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 5 25 2026-01-20", owner))
            let! couponId = getLatestCouponId ()

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage($"/debug {couponId}", admin))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            let debugResponse = findDebugReply calls
            Assert.True(debugResponse.IsSome, "Admin should receive debug output with <pre> block")
            let text = debugResponse.Value

            Assert.Contains("Номинал: 5€ из 25€", text)
            Assert.Contains("Действует с: —", text)
            Assert.Contains("Штрихкод: —", text)
            Assert.Contains("Статус: available", text)
            Assert.DoesNotContain("Взят:", text)
        }

    [<Fact>]
    let ``Debug on an unknown coupon id reports not found`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            let admin = Tg.user(id = adminId, username = "admin_debug", firstName = "Admin")
            do! fixture.SetChatMemberStatus(admin.Id, "member")

            let! resp = fixture.SendUpdate(Tg.dmMessage("/debug 987654", admin))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls adminId "Купон ID:987654 не найден.",
                        "Admin should be told the coupon does not exist")
            Assert.True((findDebugReply calls).IsNone, "No history block for a coupon that does not exist")
        }
