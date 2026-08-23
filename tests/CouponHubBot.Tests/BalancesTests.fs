namespace CouponHubBot.Tests

open System
open System.Net
open System.Text.Json
open System.Text.RegularExpressions
open BotTestInfra
open Funogram.Telegram.Types
open Xunit
open FakeCallHelpers

/// /balances is admin-only, paginated over ALL "user" rows (see the assembly-wide DB
/// fixture gotcha: the total is unbounded and racy even against a same-test COUNT query
/// — read it back from the bot's own reply instead of asserting an absolute number).
type BalancesTests(fixture: DefaultCouponHubTestContainers) =

    // Admin user ID 900 is configured in FEEDBACK_ADMINS for the test container.
    let adminId = 900L
    let mutable updateIdSeq = 700_000_000L

    let nextUpdateId () =
        updateIdSeq <- updateIdSeq + 1L
        updateIdSeq

    /// Same shape as Tg.dmCallback but with an explicit message id, so a test can assert
    /// the resulting editMessageText targets that SAME id (Tg.dmCallback always mints a fresh one).
    let dmCallbackAt (data: string) (fromUser: User) (messageId: int64) =
        let chat = Tg.privateChat(id = fromUser.Id)
        Update.Create(
            updateId = nextUpdateId(),
            callbackQuery = CallbackQuery.Create(
                id = Guid.NewGuid().ToString(),
                from = fromUser,
                chatInstance = Guid.NewGuid().ToString(),
                data = data,
                message = MaybeInaccessibleMessage.Message(
                    Message.Create(
                        messageId = messageId,
                        date = DateTime.UtcNow,
                        chat = chat,
                        from = fromUser
                    )
                )
            )
        )

    /// 20 zero-activity users, directly seeded (bypassing /add), so pagination tests always
    /// have a guaranteed page 2 regardless of how many other test files have already run.
    let seedFillerUsers () =
        fixture.Execute(
            """
INSERT INTO "user" (id, username, first_name, created_at, updated_at)
SELECT 900_000_000 + n, 'bal_filler_' || n, 'Filler' || n, NOW(), NOW()
FROM generate_series(1, 20) AS n
ON CONFLICT (id) DO NOTHING;
""", null)

    // The admin-facing /balances reply: status line + <pre> table, one message.
    let findBalancesReply (calls: FakeCall array) =
        calls |> Array.tryPick (fun call ->
            match parseCallBody call.Body with
            | Some parsed when parsed.ChatId = Some adminId && parsed.Text.IsSome && parsed.Text.Value.Contains("<pre>") ->
                Some parsed.Text.Value
            | _ -> None)

    /// First data row right after the table's `|---|` separator line (all-dash/pipe line).
    let firstDataRow (text: string) =
        let lines = text.Replace("<pre>", "").Replace("</pre>", "").Split('\n')
        let sepIdx = lines |> Array.findIndex (fun l -> l.Length > 0 && l |> Seq.forall (fun c -> c = '-' || c = '|'))
        lines[sepIdx + 1]

    /// Splits a rendered row "| a | b | c |" into trimmed per-column cell contents
    /// (columns are split, not composite "cnt·sum" cells — see formatBalancesTable).
    let cellsOfRow (row: string) =
        let parts = row.Split('|')
        parts.[1 .. parts.Length - 2] |> Array.map (fun (s: string) -> s.Trim())

    /// Column order: id, user, з#, з€, в#, в€, б#, б€, ан, рп.
    let findRowForUser (text: string) (userId: int64) =
        let lines = text.Replace("<pre>", "").Replace("</pre>", "").Split('\n')
        lines
        |> Array.tryFind (fun l -> l.StartsWith("| ") && (cellsOfRow l).[0] = string userId)
        |> Option.map cellsOfRow

    /// Fetches a rendered /balances page via the ◀/▶/sort callback path.
    let getBalancesPageText (admin: User) (page: int) (sortToken: string) =
        task {
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(dmCallbackAt $"balances:{page}:{sortToken}" admin (nextUpdateId()))
            let! edits = fixture.GetFakeCalls("editMessageText")
            use doc = JsonDocument.Parse(edits[0].Body)
            return doc.RootElement.GetProperty("text").GetString()
        }

    /// Scans every /balances page for `userId`'s row — the shared assembly-wide fixture makes
    /// the target's page number unpredictable, so this walks pages instead of assuming page 1.
    let findRowAcrossPages (admin: User) (sortToken: string) (userId: int64) =
        task {
            let! page1 = getBalancesPageText admin 1 sortToken
            let totalMatch = Regex.Match(page1, @"Стр\. 1/(\d+)")
            let totalPages = if totalMatch.Success then int totalMatch.Groups[1].Value else 1
            let mutable found = findRowForUser page1 userId
            let mutable p = 2
            while found.IsNone && p <= totalPages do
                let! text = getBalancesPageText admin p sortToken
                found <- findRowForUser text userId
                p <- p + 1
            return found
        }

    let inlineButtons (body: string) =
        use doc = JsonDocument.Parse(body)
        match doc.RootElement.TryGetProperty("reply_markup") with
        | true, rm ->
            rm.GetProperty("inline_keyboard").EnumerateArray()
            |> Seq.collect (fun row -> row.EnumerateArray())
            |> Seq.map (fun b -> b.GetProperty("text").GetString(), b.GetProperty("callback_data").GetString())
            |> Seq.toArray
        | _ -> [||]

    [<Fact>]
    let ``Admin balances page 1 shows worst-balance-first, correct values and next-page button`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let! _ = seedFillerUsers()

            let admin = Tg.user(id = adminId, username = "admin", firstName = "Admin")
            let ownerRich = Tg.user(username = "bal_owner_rich", firstName = "Rich")
            let takerPoor = Tg.user(username = "bal_taker_poor", firstName = "Poor")
            let midUser = Tg.user(username = "bal_mid", firstName = "Mid")
            let ownerHelper = Tg.user(username = "bal_owner_helper", firstName = "Helper")
            do! fixture.SetChatMemberStatus(admin.Id, "member")
            do! fixture.SetChatMemberStatus(ownerRich.Id, "member")
            do! fixture.SetChatMemberStatus(takerPoor.Id, "member")
            do! fixture.SetChatMemberStatus(midUser.Id, "member")
            do! fixture.SetChatMemberStatus(ownerHelper.Id, "member")

            // ownerRich adds 90000, never takes -> balance +90000 (biggest contributor).
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 90000 100 2026-01-25", ownerRich))
            let! richCouponId = fixture.QuerySingle<int>("SELECT id FROM coupon WHERE owner_id = @o", {| o = ownerRich.Id |})
            // takerPoor takes it, adds nothing -> balance -90000 (worst debtor).
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {richCouponId}", takerPoor))

            // midUser adds 15000 and takes 15030 -> balance -30 (a smaller debtor than takerPoor).
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 15000 50 2026-01-25", midUser))
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 15030 50 2026-01-25", ownerHelper))
            let! helperCouponId = fixture.QuerySingle<int>("SELECT id FROM coupon WHERE owner_id = @o", {| o = ownerHelper.Id |})
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {helperCouponId}", midUser))

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage("/balances", admin))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            let reply = findBalancesReply calls
            Assert.True(reply.IsSome, "Admin should get a /balances reply with a <pre> table")
            let text = reply.Value

            // The absolute user total accumulates across the whole assembly and even a
            // same-test pre-query races background work from earlier tests — read N back
            // from the bot's own reply and check ITS pagination math instead.
            let statusMatch = Regex.Match(text, @"Стр\. 1/(\d+) · всего: (\d+) · сортировка: баланс")
            Assert.True(statusMatch.Success, $"Status line not found in: {text}")
            let totalPages = int statusMatch.Groups[1].Value
            let userCount = int statusMatch.Groups[2].Value
            Assert.True(userCount >= 25, "At least the 5 seeded + 20 filler users must be counted")
            Assert.Equal(max 1 (int (ceil (float userCount / 15.0))), totalPages)

            // Worst debtor (takerPoor, -90000) must render before a smaller debtor (midUser, -30).
            let idxWorst = text.IndexOf(string takerPoor.Id)
            let idxMid = text.IndexOf(string midUser.Id)
            Assert.True(idxWorst >= 0 && idxMid >= 0, "Both seeded debtors should appear on page 1")
            Assert.True(idxWorst < idxMid, "Bigger debtor should render before a smaller debtor")

            // Count and sum are separate columns (в#/в€, б#/б€), not composite "cnt·sum" cells.
            match findRowForUser text takerPoor.Id with
            | Some cells ->
                Assert.Equal("1", cells[4])       // в# — taken 1x90000
                Assert.Equal("90000", cells[5])    // в€
                Assert.Equal("-1", cells[6])       // б# = 0 added - 1 taken
                Assert.Equal("-90000", cells[7])   // б€ = 0 - 90000
            | None -> Assert.True(false, "takerPoor row not found on page 1")

            let sendCall = calls |> Array.find (fun c -> (parseCallBody c.Body |> Option.bind (fun p -> p.Text)) = Some text)
            let buttons = inlineButtons sendCall.Body
            // Keyboard is ALWAYS exactly 3 buttons [◀] [⇅ sort] [▶], even on page 1 — the
            // edge ◀ carries the no-op callback data instead of being omitted.
            Assert.Equal(3, buttons.Length)
            Assert.Equal(("◀", "balances:noop"), buttons[0])
            Assert.True(buttons |> Array.exists (fun (t, _) -> t = "⇅ баланс"), "Sort button should show current mode")
            if totalPages > 1 then
                Assert.True(buttons |> Array.exists (fun (t, d) -> t = "▶" && d <> "balances:noop"), "▶ expected to be a real page-2 link: more than one page")
        }

    [<Fact>]
    let ``Pressing next-page arrow edits the same message and advances the page`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let! _ = seedFillerUsers()

            let admin = Tg.user(id = adminId, username = "admin", firstName = "Admin")
            do! fixture.SetChatMemberStatus(admin.Id, "member")

            let msgId = 123_456_789L
            let! resp = fixture.SendUpdate(dmCallbackAt "balances:2:balance" admin msgId)
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! sends = fixture.GetFakeCalls("sendMessage")
            let! edits = fixture.GetFakeCalls("editMessageText")
            Assert.Equal(0, sends.Length)
            Assert.Equal(1, edits.Length)

            use doc = JsonDocument.Parse(edits[0].Body)
            Assert.Equal(msgId, doc.RootElement.GetProperty("message_id").GetInt64())
            Assert.Equal(adminId, doc.RootElement.GetProperty("chat_id").GetInt64())
            let text = doc.RootElement.GetProperty("text").GetString()
            Assert.Contains("Стр. 2/", text)

            let buttons = inlineButtons (edits[0].Body)
            Assert.True(buttons |> Array.exists (fun (t, _) -> t = "◀"), "◀ expected on page 2")
        }

    [<Fact>]
    let ``Pressing the sort button cycles mode, resets to page 1 and reorders by added value`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let! _ = seedFillerUsers()

            let admin = Tg.user(id = adminId, username = "admin", firstName = "Admin")
            let ownerRich = Tg.user(username = "bal_sort_owner", firstName = "SortRich")
            do! fixture.SetChatMemberStatus(admin.Id, "member")
            do! fixture.SetChatMemberStatus(ownerRich.Id, "member")

            // Dwarfs every other user's added_value in the shared fixture -> guaranteed rank 1.
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 500000 100 2026-01-25", ownerRich))

            // The sort button's own callback data always encodes page=1 + the NEXT mode
            // (balances:1:added switches balance -> added), regardless of the page it was pressed from.
            let msgId = 987_654_321L
            let! resp = fixture.SendUpdate(dmCallbackAt "balances:1:added" admin msgId)
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! edits = fixture.GetFakeCalls("editMessageText")
            Assert.Equal(1, edits.Length)
            use doc = JsonDocument.Parse(edits[0].Body)
            Assert.Equal(msgId, doc.RootElement.GetProperty("message_id").GetInt64())
            let text = doc.RootElement.GetProperty("text").GetString()
            Assert.Contains("Стр. 1/", text)
            Assert.Contains("сортировка: залил", text)
            Assert.Contains(string ownerRich.Id, firstDataRow text)

            let buttons = inlineButtons (edits[0].Body)
            Assert.True(buttons |> Array.exists (fun (t, d) -> t = "⇅ залил" && d = "balances:1:taken"),
                "Sort button should show 'залил' and cycle next to 'taken'")
        }

    [<Fact>]
    let ``Last-page balances keyboard still has all three buttons, with a no-op arrow`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let! _ = seedFillerUsers()

            let admin = Tg.user(id = adminId, username = "admin", firstName = "Admin")
            do! fixture.SetChatMemberStatus(admin.Id, "member")

            let! resp = fixture.SendUpdate(Tg.dmMessage("/balances", admin))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
            let! sends = fixture.GetFakeCalls("sendMessage")
            let reply = findBalancesReply sends
            Assert.True(reply.IsSome, "Admin should get a /balances reply with a <pre> table")
            let statusMatch = Regex.Match(reply.Value, @"Стр\. 1/(\d+)")
            Assert.True(statusMatch.Success, $"Status line not found in: {reply.Value}")
            let totalPages = int statusMatch.Groups[1].Value
            Assert.True(totalPages > 1, "20 seeded filler users guarantee a page 2+")

            do! fixture.ClearFakeCalls()
            let msgId = 555_444_333L
            let! resp2 = fixture.SendUpdate(dmCallbackAt $"balances:{totalPages}:balance" admin msgId)
            Assert.Equal(HttpStatusCode.OK, resp2.StatusCode)
            let! edits = fixture.GetFakeCalls("editMessageText")
            Assert.Equal(1, edits.Length)

            let buttons = inlineButtons edits[0].Body
            Assert.Equal(3, buttons.Length)
            Assert.Equal(("▶", "balances:noop"), buttons[2])
            Assert.True(buttons |> Array.exists (fun (t, d) -> t = "◀" && d <> "balances:noop"), "◀ should be a real back link on the last page")
        }

    [<Fact>]
    let ``Pressing the no-op edge button stops the spinner without editing`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let admin = Tg.user(id = adminId, username = "admin", firstName = "Admin")
            do! fixture.SetChatMemberStatus(admin.Id, "member")

            let! resp = fixture.SendUpdate(dmCallbackAt "balances:noop" admin 222_333_444L)
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! sends = fixture.GetFakeCalls("sendMessage")
            let! edits = fixture.GetFakeCalls("editMessageText")
            Assert.Equal(0, sends.Length)
            Assert.Equal(0, edits.Length)
        }

    [<Fact>]
    let ``Table shape (header, separator, row lengths) is identical across pages`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let! _ = seedFillerUsers()

            let admin = Tg.user(id = adminId, username = "admin", firstName = "Admin")
            do! fixture.SetChatMemberStatus(admin.Id, "member")

            let! resp1 = fixture.SendUpdate(dmCallbackAt "balances:1:balance" admin 111_000_111L)
            Assert.Equal(HttpStatusCode.OK, resp1.StatusCode)
            let! resp2 = fixture.SendUpdate(dmCallbackAt "balances:2:balance" admin 222_000_222L)
            Assert.Equal(HttpStatusCode.OK, resp2.StatusCode)

            let! edits = fixture.GetFakeCalls("editMessageText")
            Assert.Equal(2, edits.Length)
            let textOf (call: FakeCall) =
                use doc = JsonDocument.Parse(call.Body)
                doc.RootElement.GetProperty("text").GetString()
            // Drop the "Стр. X/Y · ..." status line; remaining lines are header/sep/data rows.
            let tableLines (text: string) =
                (text.Replace("<pre>", "").Replace("</pre>", "").Split('\n')) |> Array.skip 1

            let lines1 = tableLines (textOf edits[0])
            let lines2 = tableLines (textOf edits[1])
            Assert.Equal(lines1[0], lines2[0]) // header
            Assert.Equal(lines1[1], lines2[1]) // separator
            Assert.Equal(lines1.Length, lines2.Length)
            for i in 2 .. lines1.Length - 1 do
                Assert.Equal(lines1[i].Length, lines2[i].Length)
        }

    [<Fact>]
    let ``Take-then-return nets to zero in /balances (в#/в€/б#/б€) and /whois`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            let admin = Tg.user(id = adminId, username = "admin", firstName = "Admin")
            let owner = Tg.user(username = "bal_return_owner", firstName = "ReturnOwner")
            let taker = Tg.user(username = "bal_return_taker", firstName = "ReturnTaker")
            do! fixture.SetChatMemberStatus(admin.Id, "member")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = fixture.QuerySingle<int>("SELECT id FROM coupon WHERE owner_id = @o ORDER BY id DESC LIMIT 1", {| o = owner.Id |})

            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/return {couponId}", taker))

            let! rowOpt = findRowAcrossPages admin "balance" taker.Id
            match rowOpt with
            | Some cells ->
                Assert.Equal("0", cells[4]) // в# — 1 take, 1 return, nets to 0
                Assert.Equal("0", cells[5]) // в€
                Assert.Equal("0", cells[6]) // б# — taker never added anything either
                Assert.Equal("0", cells[7]) // б€
            | None -> Assert.True(false, "taker row not found across any /balances page")

            do! fixture.ClearFakeCalls()
            let! whoisResp = fixture.SendUpdate(Tg.dmMessage($"/whois {taker.Id}", admin))
            Assert.Equal(HttpStatusCode.OK, whoisResp.StatusCode)
            let! calls = fixture.GetFakeCalls("sendMessage")
            let whoisText =
                calls
                |> Array.tryPick (fun c ->
                    match parseCallBody c.Body with
                    | Some parsed when parsed.ChatId = Some adminId && parsed.Text.IsSome && parsed.Text.Value.Contains("<pre>") -> parsed.Text
                    | _ -> None)
            Assert.True(whoisText.IsSome, "Admin should get a /whois reply for the taker")
            Assert.Contains("Взято: 0 · 0€", whoisText.Value)
            Assert.Contains("Баланс: 0 · 0€", whoisText.Value)
        }

    [<Fact>]
    let ``Voided/reported coupons count against the OWNER, not the voiding admin or the reporter`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            let admin = Tg.user(id = adminId, username = "admin", firstName = "Admin")
            let owner = Tg.user(username = "bal_owner_flags", firstName = "OwnerFlags")
            let taker = Tg.user(username = "bal_taker_flags", firstName = "TakerFlags")
            do! fixture.SetChatMemberStatus(admin.Id, "member")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            // owner adds two coupons: one gets voided by the ADMIN (not the owner), one gets
            // taken then reported by the TAKER.
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! voidedCouponId = fixture.QuerySingle<int>("SELECT id FROM coupon WHERE owner_id = @o ORDER BY id DESC LIMIT 1", {| o = owner.Id |})
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 20 60 2026-01-25", owner))
            let! reportedCouponId = fixture.QuerySingle<int>("SELECT id FROM coupon WHERE owner_id = @o ORDER BY id DESC LIMIT 1", {| o = owner.Id |})

            let! _ = fixture.SendUpdate(Tg.dmMessage($"/void {voidedCouponId}", admin))
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {reportedCouponId}", taker))
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/report {reportedCouponId}", taker))

            let! rowOpt = findRowAcrossPages admin "balance" owner.Id
            match rowOpt with
            | Some cells ->
                Assert.Equal("1", cells[8]) // ан — 'voided' event carries the owner's user_id already, even though the admin acted
                Assert.Equal("1", cells[9]) // рп — 'reported' event carries the taker's user_id; joined via coupon.owner_id to land on the owner
            | None -> Assert.True(false, "owner row not found across any /balances page")
        }

    [<Fact>]
    let ``Non-admin user sends balances command and gets no response`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let user = Tg.user(username = "regular_balances", firstName = "Regular")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let! resp = fixture.SendUpdate(Tg.dmMessage("/balances", user))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.Equal(0, calls.Length)
        }

    [<Fact>]
    let ``Non-admin pressing a balances callback is silent (no edit, no send)`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let user = Tg.user(username = "regular_balances2", firstName = "Regular2")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let! resp = fixture.SendUpdate(dmCallbackAt "balances:1:balance" user 111_222_333L)
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! sends = fixture.GetFakeCalls("sendMessage")
            let! edits = fixture.GetFakeCalls("editMessageText")
            Assert.Equal(0, sends.Length)
            Assert.Equal(0, edits.Length)
        }
