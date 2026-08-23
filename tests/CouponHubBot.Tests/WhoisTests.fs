namespace CouponHubBot.Tests

open BotTestInfra
open System.Net
open Xunit
open FakeCallHelpers

type WhoisTests(fixture: DefaultCouponHubTestContainers) =

    // Admin user ID 900 is configured in FEEDBACK_ADMINS for the test container.
    let adminId = 900L

    // The admin-facing /whois reply: header + <pre> actions block, one message.
    let findWhoisReply (calls: FakeCall array) =
        calls |> Array.tryPick (fun call ->
            match parseCallBody call.Body with
            | Some parsed when parsed.ChatId = Some adminId && parsed.Text.IsSome && parsed.Text.Value.Contains("<pre>") ->
                Some parsed.Text.Value
            | _ -> None)

    [<Fact>]
    let ``Admin whois by id shows identity, membership, give/take counts and value sums`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            let admin = Tg.user(id = adminId, username = "admin", firstName = "Admin")
            let target = Tg.user(id = 740L, username = "whois_target", firstName = "Target")
            let owner2 = Tg.user(id = 741L, username = "whois_owner2", firstName = "Owner2")
            do! fixture.SetChatMemberStatus(admin.Id, "member")
            do! fixture.SetChatMemberStatus(target.Id, "member")
            do! fixture.SetChatMemberStatus(owner2.Id, "member")

            // target adds one coupon (10€ / 50€ min check)
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", target))
            // owner2 adds a coupon (5€ / 25€) that target then takes
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 5 25 2026-01-25", owner2))
            let! takenCouponId =
                fixture.QuerySingle<int>("SELECT id FROM coupon WHERE owner_id = @o", {| o = owner2.Id |})
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {takenCouponId}", target))

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage($"/whois {target.Id}", admin))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            let whoisResponse = findWhoisReply calls
            Assert.True(whoisResponse.IsSome, "Admin should receive a /whois reply with a <pre> block")
            let text = whoisResponse.Value

            Assert.Contains($"id:{target.Id}", text)
            Assert.Contains("@whois_target", text)
            Assert.Contains("В комьюнити: да", text)
            Assert.Contains("Добавлено: 1 · 10€", text)
            Assert.Contains("Взято: 1 · 5€", text)
            Assert.Contains("Баланс: 0 · 5€", text)
            Assert.Contains("added", text)
            Assert.Contains("taken", text)
        }

    [<Fact>]
    let ``Whois Взято nets to zero for a take-then-return (matches /balances semantics)`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            let admin = Tg.user(id = adminId, username = "admin", firstName = "Admin")
            let owner = Tg.user(id = 747L, username = "whois_return_owner", firstName = "Owner")
            let target = Tg.user(id = 748L, username = "whois_return_taker", firstName = "Taker")
            do! fixture.SetChatMemberStatus(admin.Id, "member")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(target.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = fixture.QuerySingle<int>("SELECT id FROM coupon WHERE owner_id = @o ORDER BY id DESC LIMIT 1", {| o = owner.Id |})
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", target))
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/return {couponId}", target))

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage($"/whois {target.Id}", admin))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            let whoisResponse = findWhoisReply calls
            Assert.True(whoisResponse.IsSome, "Admin should get a /whois reply with a <pre> block")
            let text = whoisResponse.Value

            // 'returned' nets the 'taken' event out (both count and sum) — a take-then-return
            // must not show up as a debt, here or in /balances.
            Assert.Contains("Взято: 0 · 0€", text)
            Assert.Contains("Баланс: 0 · 0€", text)
        }

    [<Fact>]
    let ``Admin whois by username resolves case-insensitively`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            let admin = Tg.user(id = adminId, username = "admin", firstName = "Admin")
            let target = Tg.user(id = 742L, username = "Whois_Case", firstName = "Case")
            do! fixture.SetChatMemberStatus(admin.Id, "member")
            do! fixture.SetChatMemberStatus(target.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", target))

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage("/whois @whois_case", admin))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            let whoisResponse = findWhoisReply calls
            Assert.True(whoisResponse.IsSome, "Username lookup should be case-insensitive and @ optional")
            Assert.Contains($"id:{target.Id}", whoisResponse.Value)
        }

    [<Fact>]
    let ``Non-admin user sends whois command and gets no response`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let user = Tg.user(id = 743L, username = "regular_whois", firstName = "Regular")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let! resp = fixture.SendUpdate(Tg.dmMessage("/whois 900", user))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.Equal(0, calls.Length)
        }

    [<Fact>]
    let ``Whois on an unknown user reports not found`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            let admin = Tg.user(id = adminId, username = "admin", firstName = "Admin")
            do! fixture.SetChatMemberStatus(admin.Id, "member")

            let! resp = fixture.SendUpdate(Tg.dmMessage("/whois 987654321", admin))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls adminId "Пользователь не найден",
                        "Admin should be told the user does not exist")
            Assert.True((findWhoisReply calls).IsNone, "No <pre> block for an unknown user")
        }

    [<Fact>]
    let ``Whois multi word name query returns a fuzzy candidate list`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            let admin = Tg.user(id = adminId, username = "admin", firstName = "Admin")
            let target = Tg.user(id = 744L, username = "elena_s", firstName = "Elena")
            do! fixture.SetChatMemberStatus(admin.Id, "member")
            do! fixture.SetChatMemberStatus(target.Id, "member")
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", target))
            let! _ = fixture.Execute("""UPDATE "user" SET last_name = @ln WHERE id = @id""", {| ln = "Sokolenko"; id = target.Id |})

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage("/whois Elena Sokolova", admin))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls adminId "Найдены похожие юзеры",
                        "A close-but-not-exact name should surface the fuzzy candidate list")
            Assert.True(findCallWithText calls adminId $"id:{target.Id}",
                        "Elena Sokolenko should be listed as a fuzzy candidate for 'Elena Sokolova'")
            Assert.True((findWhoisReply calls).IsNone, "Fuzzy fallback is a list, not the full <pre> profile")
        }

    [<Fact>]
    let ``Whois digit substring query returns a fuzzy candidate list`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            let admin = Tg.user(id = adminId, username = "admin", firstName = "Admin")
            // Long, distinctive digit run so this can't collide with other tests' ids
            // sharing the assembly-wide DB fixture.
            let target = Tg.user(id = 918273645L, username = "id_target", firstName = "IdTarget")
            do! fixture.SetChatMemberStatus(admin.Id, "member")
            do! fixture.SetChatMemberStatus(target.Id, "member")
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", target))

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage("/whois 182736", admin))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls adminId "Найдены похожие юзеры",
                        "A digit substring of a user id should surface the fuzzy candidate list")
            Assert.True(findCallWithText calls adminId $"id:{target.Id}")
        }

    [<Fact>]
    let ``Whois garbage query reports not found`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            let admin = Tg.user(id = adminId, username = "admin", firstName = "Admin")
            let target = Tg.user(id = 745L, username = "elena_s2", firstName = "Elena")
            do! fixture.SetChatMemberStatus(admin.Id, "member")
            do! fixture.SetChatMemberStatus(target.Id, "member")
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", target))
            let! _ = fixture.Execute("""UPDATE "user" SET last_name = @ln WHERE id = @id""", {| ln = "Sokolenko"; id = target.Id |})

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage("/whois zzzzqqq", admin))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls adminId "Пользователь не найден")
        }

    [<Fact>]
    let ``Non-admin fuzzy whois name query gets no response`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let user = Tg.user(id = 746L, username = "regular_whois2", firstName = "Regular")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let! resp = fixture.SendUpdate(Tg.dmMessage("/whois Elena Sokolova", user))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.Equal(0, calls.Length)
        }
