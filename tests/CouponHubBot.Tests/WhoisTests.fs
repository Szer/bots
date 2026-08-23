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
