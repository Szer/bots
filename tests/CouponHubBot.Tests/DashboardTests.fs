namespace CouponHubBot.Tests

open System
open System.Globalization
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks
open BotInfra
open BotTestInfra
open CouponHubBot.Utils
open Dapper
open Npgsql
open Xunit

module private DashboardTestData =
    let instant (value: string) = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture)

    let sign botToken (fields: (string * string) list) =
        let sorted = fields |> List.sortWith (fun (a, _) (b, _) -> StringComparer.Ordinal.Compare(a, b))
        let data = sorted |> List.map (fun (key, value) -> key + "=" + value) |> String.concat "\n"
        let secret = HMACSHA256.HashData(Encoding.UTF8.GetBytes "WebAppData", Encoding.UTF8.GetBytes(botToken: string))
        let signature = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes data) |> Convert.ToHexStringLower
        (fields @ [ "hash", signature ])
        |> List.map (fun (key, value) -> Uri.EscapeDataString key + "=" + Uri.EscapeDataString value)
        |> String.concat "&"

    let fields (now: DateTimeOffset) (userId: int64) =
        [ "auth_date", string (now.ToUnixTimeSeconds())
          "query_id", "synthetic+query/value"
          "user", JsonSerializer.Serialize({| id = userId; first_name = "Имя + & / < >" |}) ]

    let proof now userId = sign "123:456" (fields now userId)

    let json (response: HttpResponseMessage) =
        task {
            let! text = response.Content.ReadAsStringAsync()
            use doc = JsonDocument.Parse text
            return doc.RootElement.Clone()
        }

    let memberRow userId (body: JsonElement) =
        body.GetProperty("members").EnumerateArray()
        |> Seq.find (fun row -> row.GetProperty("id").GetString() = string userId)

    let integer name expected (row: JsonElement) = Assert.Equal(expected, row.GetProperty(name: string).GetInt32())
    let number name expected (row: JsonElement) = Assert.Equal(expected, row.GetProperty(name: string).GetDouble(), 2)

type DashboardTestContainers() =
    inherit CouponHubTestContainers(seedExpiringToday = false, ocrEnabled = false, instanceCount = 2)

    let mutable now = DashboardTestData.instant "2026-01-01T00:00:00Z"

    member _.Now = now

    member this.AdvanceDashboardClock(seconds: int) =
        task {
            do! this.AdvanceAllClocks(seconds * 1000)
            now <- now.AddSeconds(float seconds)
        }

    override this.SeedDatabase(connString: string) =
        let seed = base.SeedDatabase(connString)
        task {
            do! seed
            use conn = new NpgsqlConnection(connString)
            do! conn.OpenAsync()
            do! conn.ExecuteAsync("UPDATE bot_setting SET value = 'true' WHERE key = 'DASHBOARD_ENABLED'") :> Task
            do! conn.ExecuteAsync(
                "UPDATE bot_setting SET value = @url WHERE key = 'DASHBOARD_URL'",
                {| url = "https://coupon.example/dashboard" |}) :> Task
            for id, name in [ 20001L, "Alex"; 20002L, "Rowan"; 20003L, "Sky"; 20004L, "No activity"; 20005L, "Admin" ] do
                do! conn.ExecuteAsync(
                    """
INSERT INTO "user" (id, username, first_name, created_at, updated_at)
VALUES (@id, @username, @name, @created, @created)
                    """, {| id = id; username = "synthetic_" + string id; name = name; created = DashboardTestData.instant "2025-01-01T00:00:00Z" |}) :> Task

            let addCoupon owner expiry events =
                task {
                    let! couponId = conn.QuerySingleAsync<int>(
                        """
INSERT INTO coupon (owner_id, photo_file_id, value, min_check, expires_at, status)
VALUES (@owner, @photo, 10, 50, @expiry::date, 'available') RETURNING id
                        """, {| owner = owner; expiry = expiry; photo = "synthetic-dashboard-" + Guid.NewGuid().ToString("N") |})
                    for userId, eventType, at in events do
                        do! conn.ExecuteAsync(
                            """
INSERT INTO coupon_event (coupon_id, user_id, event_type, created_at)
VALUES (@couponId, @userId, @eventType, @at)
                            """, {| couponId = couponId; userId = userId; eventType = eventType; at = DashboardTestData.instant at |}) :> Task
                }

            do! addCoupon 20001L "2025-11-10"
                    [ 20001L, "added", "2025-11-01T10:00:00Z"
                      20002L, "taken", "2025-11-05T10:00:00Z"
                      20002L, "returned", "2025-11-06T12:00:00Z"
                      20003L, "taken", "2025-11-06T13:00:00Z"
                      20003L, "used", "2025-11-06T15:00:00Z" ]
            do! addCoupon 20001L "2025-11-10"
                    [ 20001L, "added", "2025-11-02T10:00:00Z"
                      20002L, "taken", "2025-11-07T10:00:00Z"
                      20002L, "returned", "2025-11-08T10:00:00Z"
                      20005L, "returned_reverted", "2025-11-08T11:00:00Z"
                      20002L, "used", "2025-11-09T10:00:00Z" ]
            do! addCoupon 20001L "2025-11-10"
                    [ 20001L, "added", "2025-11-03T10:00:00Z"
                      20002L, "taken", "2025-11-07T10:00:00Z"
                      20005L, "taken_reverted", "2025-11-07T11:00:00Z"
                      20003L, "taken", "2025-11-09T10:00:00Z"
                      20003L, "reported", "2025-11-09T13:00:00Z"
                      20001L, "used", "2025-11-09T14:00:00Z" ]
            do! addCoupon 20001L "2025-11-10"
                    [ 20001L, "added", "2025-11-04T10:00:00Z"
                      20002L, "taken", "2025-11-08T10:00:00Z"
                      20001L, "voided", "2025-11-09T10:00:00Z" ]
            do! addCoupon 20001L "2025-11-10"
                    [ 20001L, "added", "2025-11-04T11:00:00Z"
                      20002L, "taken", "2025-11-09T10:00:00Z" ]
            do! addCoupon 20001L "2025-11-10"
                    [ 20001L, "added", "2025-11-09T10:00:00Z"
                      20002L, "taken", "2025-11-09T11:00:00Z"
                      20002L, "returned", "2025-11-10T12:00:00Z" ]
            do! addCoupon 20003L "2025-07-03" [ 20003L, "added", "2025-07-01T23:30:00Z" ]
        } :> Task

type DashboardDefaultFlagTests(fixture: DefaultCouponHubTestContainers) =
    [<Fact>]
    let ``Dashboard feature flag is seeded off and loaded off by default`` () =
        task {
            let! stored = fixture.QuerySingle<string>("SELECT value FROM bot_setting WHERE key = 'DASHBOARD_ENABLED'", null)
            Assert.Equal("false", stored)
            let! dump = fixture.GetSettingsDump 0
            use parsed = JsonDocument.Parse dump
            Assert.False(parsed.RootElement.GetProperty("DashboardEnabled").GetBoolean())
        }

type DashboardTests(fixture: DashboardTestContainers) =
    let setDashboardEnabled enabled =
        task {
            let! _ = fixture.Execute("UPDATE bot_setting SET value = @value WHERE key = 'DASHBOARD_ENABLED'",
                                     {| value = if enabled then "true" else "false" |})
            use content = new StringContent("")
            use! response = fixture.Bot.PostAsync("/reload-settings", content)
            %response.EnsureSuccessStatusCode()
            let deadline = DateTime.UtcNow.AddSeconds 5.0
            let mutable propagated = false
            while not propagated && DateTime.UtcNow < deadline do
                let! dump = fixture.GetSettingsDump 1
                use parsed = JsonDocument.Parse dump
                propagated <- parsed.RootElement.GetProperty("DashboardEnabled").GetBoolean() = enabled
                if not propagated then do! Task.Delay 25
            Assert.True(propagated, "Dashboard flag must propagate to the other replica")
        }

    let withDashboardDisabled action =
        task {
            do! setDashboardEnabled false
            try
                do! action()
            with error ->
                do! setDashboardEnabled true
                return raise error
            do! setDashboardEnabled true
        }

    let postProof pod proof =
        task {
            use content = new StringContent(proof, Encoding.UTF8, "text/plain")
            return! fixture.BotHttpAt(pod).PostAsync("/dashboard/api/session", content)
        }

    let login userId =
        task {
            use! response = postProof 0 (DashboardTestData.proof fixture.Now userId)
            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            let! body = DashboardTestData.json response
            Assert.Equal(string userId, body.GetProperty("viewerId").GetString())
            return body.GetProperty("token").GetString()
        }

    let getStats pod token days =
        task {
            use request = new HttpRequestMessage(HttpMethod.Get, $"/dashboard/api/stats?days={days}")
            request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
            return! fixture.BotHttpAt(pod).SendAsync request
        }

    let snapshot days =
        task {
            let! token = login 21000L
            use! response = getStats 0 token days
            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            return! DashboardTestData.json response
        }

    [<Fact>]
    let ``Disabled dashboard blocks new and existing member sessions on both replicas but allows admins`` () =
        task {
            let! memberToken = login 22001L
            let! adminToken = login 900L
            use! warm = getStats 0 memberToken 0
            Assert.Equal(HttpStatusCode.OK, warm.StatusCode)
            do! withDashboardDisabled (fun () -> task {
                for pod in [ 0; 1 ] do
                    use! deniedLogin = postProof pod (DashboardTestData.proof fixture.Now 22001L)
                    Assert.Equal(HttpStatusCode.Forbidden, deniedLogin.StatusCode)
                    let! reason = DashboardTestData.json deniedLogin
                    Assert.Equal("dashboard_disabled", reason.GetProperty("error").GetString())
                    use! deniedStats = getStats pod memberToken 0
                    Assert.Equal(HttpStatusCode.Forbidden, deniedStats.StatusCode)
                    use! adminLogin = postProof pod (DashboardTestData.proof fixture.Now 900L)
                    Assert.Equal(HttpStatusCode.OK, adminLogin.StatusCode)
                    use! adminStats = getStats pod adminToken 0
                    Assert.Equal(HttpStatusCode.OK, adminStats.StatusCode)
            })
            use! enabledAgain = getStats 1 memberToken 0
            Assert.Equal(HttpStatusCode.OK, enabledAgain.StatusCode)
        }

    [<Fact>]
    let ``Admin bypass does not bypass Telegram authentication or community membership`` () =
        withDashboardDisabled (fun () -> task {
            do! fixture.SetChatMemberStatus(901L, "left")
            use! forged = postProof 0 (DashboardTestData.sign "123:wrong" (DashboardTestData.fields fixture.Now 900L))
            Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode)
            use! nonmemberAdmin = postProof 0 (DashboardTestData.proof fixture.Now 901L)
            Assert.Equal(HttpStatusCode.Forbidden, nonmemberAdmin.StatusCode)
        })

    [<Fact>]
    let ``Disabled dashboard commands only issue launch buttons to admins`` () =
        withDashboardDisabled (fun () -> task {
            for command in [ "/dashboard"; "/start dashboard" ] do
                do! fixture.ClearFakeCalls()
                for userId in [ 22002L; 900L ] do
                    use! response = fixture.SendUpdate(Tg.dmMessage(command, Tg.user(id = userId)))
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode)
                let! calls = fixture.GetFakeCalls "sendMessage"
                for userId, expected in [ 22002L, false; 900L, true ] do
                    let hasButton = calls |> Array.exists (fun call ->
                        use doc = JsonDocument.Parse call.Body
                        doc.RootElement.GetProperty("chat_id").GetInt64() = userId
                        && (doc.RootElement.TryGetProperty "reply_markup" |> fst))
                    Assert.Equal(expected, hasButton)
        })

    [<Fact>]
    let ``Public shell and assets contain no private statistics and API requires bearer session`` () =
        task {
            for path in [ "/dashboard"; "/dashboard/"; "/dashboard/app.js"; "/dashboard/styles.css" ] do
                use! response = fixture.Bot.GetAsync path
                Assert.Equal(HttpStatusCode.OK, response.StatusCode)
                Assert.True(response.Headers.CacheControl.NoStore)
                Assert.True(response.Headers.Contains "Content-Security-Policy")
                let! body = response.Content.ReadAsStringAsync()
                Assert.DoesNotContain("synthetic_20001", body)
            for path in [ "/dashboard/api/stats?days=0"; "/dashboard/api/stats?days=0&user_id=20001" ] do
                use! response = fixture.Bot.GetAsync path
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode)
                Assert.True(response.Headers.CacheControl.NoStore)
            use! forged = getStats 0 (String('a', 43)) 0
            Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode)
            use! infoResponse = fixture.Bot.GetAsync "/dashboard/api/info"
            Assert.Equal(HttpStatusCode.OK, infoResponse.StatusCode)
            let! info = DashboardTestData.json infoResponse
            Assert.Equal("https://t.me/coupon_test_bot?start=dashboard", info.GetProperty("botUrl").GetString())
        }

    [<Fact>]
    let ``Session is shared between replicas and stores only a hash`` () =
        task {
            let! token = login 21001L
            Assert.Equal(43, token.Length)
            use! response = getStats 1 token 0
            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            let! body = DashboardTestData.json response
            Assert.Equal("21001", body.GetProperty("viewerId").GetString())
            let! matches = fixture.QuerySingle<int>(
                "SELECT count(*)::int FROM dashboard_session WHERE token_hash = @hash AND user_id = @id",
                {| hash = SHA256.HashData(Encoding.UTF8.GetBytes token); id = 21001L |})
            Assert.Equal(1, matches)
            let! plaintext = fixture.QuerySingle<int>(
                "SELECT count(*)::int FROM dashboard_session WHERE token_hash = @raw",
                {| raw = Encoding.UTF8.GetBytes token |})
            Assert.Equal(0, plaintext)
        }

    [<Fact>]
    let ``Invalid or ambiguous Telegram identity is rejected before membership lookup`` () =
        task {
            do! fixture.ClearFakeCalls()
            let fields = DashboardTestData.fields fixture.Now 21002L
            let valid = DashboardTestData.sign "123:456" fields
            let malformed =
                [ ""
                  "user_id=21002"
                  valid.Replace("21002", "21003")
                  DashboardTestData.sign "123:wrong" fields
                  valid + "&auth_date=1"
                  valid + "&%75ser=%7B%22id%22%3A21003%7D"
                  valid + "&hash=" + String('0', 64)
                  DashboardTestData.sign "123:456" (fields |> List.filter (fun (key, _) -> key <> "user"))
                  DashboardTestData.sign "123:456" (fields |> List.filter (fun (key, _) -> key <> "auth_date"))
                  DashboardTestData.sign "123:456" [ "auth_date", string (fixture.Now.ToUnixTimeSeconds()); "user", "{\"id\":21002,\"id\":21003}" ]
                  DashboardTestData.sign "123:456" [ "auth_date", string (fixture.Now.ToUnixTimeSeconds()); "user", "{\"id\":-1}" ]
                  DashboardTestData.sign "123:456" [ "auth_date", string (fixture.Now.ToUnixTimeSeconds()); "user", "null" ]
                  String('x', 16385) ]
            for proof in malformed do
                use! response = postProof 0 proof
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode)
            let! calls = fixture.GetFakeCalls "getChatMember"
            Assert.Empty calls
        }

    [<Theory>]
    [<InlineData(-301)>]
    [<InlineData(31)>]
    let ``Expired and future Telegram proofs cannot create sessions`` (seconds: int) =
        task {
            use! response = postProof 0 (DashboardTestData.proof (fixture.Now.AddSeconds(float seconds)) 21004L)
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode)
        }

    [<Theory>]
    [<InlineData("left", 21005L)>]
    [<InlineData("kicked", 21006L)>]
    let ``Valid Telegram identity does not grant access to nonmembers`` (status: string, userId: int64) =
        task {
            do! fixture.SetChatMemberStatus(userId, status)
            use! response = postProof 0 (DashboardTestData.proof fixture.Now userId)
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode)
        }

    [<Fact>]
    let ``Membership service failure denies login`` () =
        task {
            do! fixture.SetMethodError("getChatMember", true)
            use! response = postProof 0 (DashboardTestData.proof fixture.Now 21012L)
            do! fixture.SetMethodError("getChatMember", false)
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode)
        }

    [<Fact>]
    let ``Leaving member loses access on both replicas after bounded membership cache`` () =
        task {
            let userId = 21007L
            let! token = login userId
            use! warm = getStats 1 token 0
            Assert.Equal(HttpStatusCode.OK, warm.StatusCode)
            do! fixture.SetChatMemberStatus(userId, "left")
            do! fixture.AdvanceDashboardClock 61
            for pod in [ 0; 1 ] do
                use! response = getStats pod token 0
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode)
        }

    [<Fact>]
    let ``Session expires after fifteen minutes on both replicas`` () =
        task {
            let! token = login 21008L
            do! fixture.AdvanceDashboardClock 901
            for pod in [ 0; 1 ] do
                use! response = getStats pod token 0
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode)
        }

    [<Fact>]
    let ``Sessions are bound to their configured community`` () =
        task {
            let! token = login 21009L
            let! _ = fixture.Execute(
                "UPDATE bot_setting SET value = @value WHERE key = 'COMMUNITY_CHAT_ID'", {| value = "-43" |})
            for pod in [ 0; 1 ] do
                use content = new StringContent("")
                use! reload = fixture.BotHttpAt(pod).PostAsync("/reload-settings", content)
                %reload.EnsureSuccessStatusCode()
                use! response = getStats pod token 0
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode)
            let! _ = fixture.Execute(
                "UPDATE bot_setting SET value = @value WHERE key = 'COMMUNITY_CHAT_ID'", {| value = string fixture.CommunityChatId |})
            for pod in [ 0; 1 ] do
                use content = new StringContent("")
                use! reload = fixture.BotHttpAt(pod).PostAsync("/reload-settings", content)
                %reload.EnsureSuccessStatusCode()
        }

    [<Fact>]
    let ``Snapshots reconstruct each reservation and use pooled community averages`` () =
        task {
            let! body = snapshot 0
            let rowan = DashboardTestData.memberRow 20002L body
            DashboardTestData.integer "takes" 5 rowan
            DashboardTestData.number "takeDays" 2.4 rowan
            DashboardTestData.integer "resolved" 3 rowan
            DashboardTestData.number "holdHours" 33.0 rowan
            DashboardTestData.integer "returns" 2 rowan
            DashboardTestData.number "returnPct" 40.0 rowan
            DashboardTestData.number "returnHoldHours" 25.5 rowan
            DashboardTestData.number "returnDays" 2.0 rowan
            DashboardTestData.integer "heldDayLateReturns" 1 rowan
            DashboardTestData.integer "lateReturns" 1 rowan
            DashboardTestData.integer "unresolved" 1 rowan
            DashboardTestData.integer "unresolvedExpired" 1 rowan
            DashboardTestData.integer "voidClosed" 1 rowan
            let sky = DashboardTestData.memberRow 20003L body
            DashboardTestData.integer "takes" 2 sky
            DashboardTestData.number "holdHours" 2.5 sky
            DashboardTestData.number "addDays" 1.0 sky
            DashboardTestData.integer "lateAdds" 1 sky
            let community = body.GetProperty "community"
            DashboardTestData.integer "takes" 7 community
            DashboardTestData.number "takeDays" 2.43 community
            DashboardTestData.integer "resolved" 5 community
            DashboardTestData.number "holdHours" 20.8 community
            DashboardTestData.number "returnPct" 28.57 community
            DashboardTestData.integer "adds" 7 community
            DashboardTestData.number "addDays" 5.43 community
            DashboardTestData.integer "lateAdds" 2 community
            DashboardTestData.number "lateAddPct" 28.57 community
            let idle = DashboardTestData.memberRow 20004L body
            DashboardTestData.integer "takes" 0 idle
            Assert.Equal(JsonValueKind.Null, idle.GetProperty("holdHours").ValueKind)
            Assert.Equal(JsonValueKind.Null, idle.GetProperty("addDays").ValueKind)
            let admin = DashboardTestData.memberRow 20005L body
            DashboardTestData.integer "takes" 0 admin
        }

    [<Fact>]
    let ``Periods select take and add cohorts and empty periods retain null means`` () =
        task {
            let! recent = snapshot 30
            let community = recent.GetProperty "community"
            DashboardTestData.integer "takes" 0 community
            DashboardTestData.integer "adds" 0 community
            Assert.Equal(JsonValueKind.Null, community.GetProperty("takeDays").ValueKind)
            Assert.Equal(JsonValueKind.Null, community.GetProperty("returnPct").ValueKind)
            let! quarter = snapshot 90
            let community = quarter.GetProperty "community"
            DashboardTestData.integer "takes" 7 community
            DashboardTestData.integer "adds" 6 community
            DashboardTestData.number "addDays" 6.17 community
            DashboardTestData.integer "lateAdds" 1 community
            let! token = login 21010L
            for invalid in [ -1; 1; 10000 ] do
                use! response = getStats 0 token invalid
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode)
        }

    [<Fact>]
    let ``Cached statistics stay stable until their advertised refresh time`` () =
        task {
            let! first = snapshot 365
            let oldName = (DashboardTestData.memberRow 20004L first).GetProperty("name").GetString()
            let! _ = fixture.Execute("UPDATE \"user\" SET first_name = @name WHERE id = @id",
                                     {| name = "Changed synthetic name"; id = 20004L |})
            let! cached = snapshot 365
            Assert.Equal(first.GetProperty("asOf").GetString(), cached.GetProperty("asOf").GetString())
            Assert.Equal(oldName, (DashboardTestData.memberRow 20004L cached).GetProperty("name").GetString())
            do! fixture.AdvanceDashboardClock 901
            let! fresh = snapshot 365
            Assert.NotEqual<string>(first.GetProperty("asOf").GetString(), fresh.GetProperty("asOf").GetString())
            Assert.Equal("Changed synthetic name", (DashboardTestData.memberRow 20004L fresh).GetProperty("name").GetString())
        }

    [<Theory>]
    [<InlineData("/dashboard")>]
    [<InlineData("/start dashboard")>]
    let ``Bot dashboard entry points send a private Mini App button`` (command: string) =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 21011L, username = "dashboard_synthetic")
            use! response = fixture.SendUpdate(Tg.dmMessage(command, user))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            let! calls = fixture.GetFakeCalls "sendMessage"
            let buttons =
                calls
                |> Array.choose (fun call ->
                    use doc = JsonDocument.Parse call.Body
                    let root = doc.RootElement
                    match root.TryGetProperty "reply_markup" with
                    | true, markup when root.GetProperty("chat_id").GetInt64() = user.Id ->
                        Some(markup.GetProperty("inline_keyboard").[0].[0].GetProperty("web_app").GetProperty("url").GetString())
                    | _ -> None)
            Assert.Contains("https://coupon.example/dashboard", buttons)
        }
