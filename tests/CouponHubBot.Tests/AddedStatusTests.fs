namespace CouponHubBot.Tests

open BotTestInfra
open System
open System.Globalization
open System.Net
open System.Threading.Tasks
open Dapper
open Npgsql
open Xunit
open FakeCallHelpers

/// Tests for the /added visibility-status suffixes (Shown/Taken/Reported/NotYetValid/Waiting/
/// WaitingGeneric/WaitingUnknown) added on top of pickCouponsForList, so owners can tell whether
/// their coupon is currently shown by /list, still queued for a slot, or not yet valid.
type AddedStatusTests(fixture: DefaultCouponHubTestContainers) =

    let seedUser (conn: NpgsqlConnection) (id: int64) (username: string) =
        conn.ExecuteAsync(
            """
INSERT INTO "user"(id, username, first_name, created_at, updated_at)
VALUES (@id, @uname, @uname, NOW(), NOW())
ON CONFLICT (id) DO NOTHING;
"""
            , {| id = id; uname = username |})
        :> Task

    /// Inserts a coupon directly (bypassing the /add wizard) so tests can control
    /// owner/min_check/expires_at/valid_from/status precisely. valid_from/taken_by default to
    /// SQL NULL (column defaults) when None — avoids feeding F# option values into Dapper params.
    let seedCoupon
        (conn: NpgsqlConnection)
        (ownerId: int64)
        (photoFileId: string)
        (value: decimal)
        (minCheck: decimal)
        (expiresAt: DateOnly)
        (status: string)
        (validFrom: DateOnly option)
        (takenBy: int64 option)
        =
        task {
            let expiresIso = expiresAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            do!
                conn.ExecuteAsync(
                    """
INSERT INTO coupon(owner_id, photo_file_id, value, min_check, expires_at, status)
VALUES (@owner_id, @photo_file_id, @value, @min_check, @expires_at::date, @status);
"""
                    , {| owner_id = ownerId
                         photo_file_id = photoFileId
                         value = value
                         min_check = minCheck
                         expires_at = expiresIso
                         status = status |}
                )
                :> Task
            match validFrom with
            | Some vf ->
                let vfIso = vf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                do!
                    conn.ExecuteAsync(
                        "UPDATE coupon SET valid_from = @vf::date WHERE photo_file_id = @p",
                        {| vf = vfIso; p = photoFileId |})
                    :> Task
            | None -> ()
            match takenBy with
            | Some tb ->
                do!
                    conn.ExecuteAsync(
                        "UPDATE coupon SET taken_by = @tb WHERE photo_file_id = @p",
                        {| tb = tb; p = photoFileId |})
                    :> Task
            | None -> ()
        }

    let getCouponIdByPhoto (photoFileId: string) =
        fixture.QuerySingle<int>("SELECT id FROM coupon WHERE photo_file_id = @p", {| p = photoFileId |})

    /// Extracts the /added line ("N. ID:{id} — ...") containing the given coupon id from the
    /// first DM sent to `chatId`, so assertions can target one specific coupon's status suffix
    /// without being confused by other coupons in the same listing.
    let getAddedLineForCoupon (calls: FakeCall array) (chatId: int64) (couponId: int) =
        calls
        |> Array.choose (fun c ->
            match parseCallBody c.Body with
            | Some p when p.ChatId = Some chatId -> p.Text
            | _ -> None)
        |> Array.tryHead
        |> Option.bind (fun text ->
            text.Split('\n')
            |> Array.tryFind (fun line -> line.Contains($"ID:{couponId} ")))

    [<Fact>]
    let ``/added shows (в списке) for a coupon expiring today`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 98001L, username = "added_status_today")
            do! fixture.SetChatMemberStatus(owner.Id, "member")

            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            do! conn.OpenAsync()
            do! seedUser conn owner.Id "added_status_today"
            do! seedCoupon conn owner.Id "as-today-1" 10.00m 50.00m fixture.FixedToday "available" None None

            let! couponId = getCouponIdByPhoto "as-today-1"

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage("/added", owner))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            let line = getAddedLineForCoupon calls owner.Id couponId
            Assert.True(line.IsSome, "Expected an /added line for the seeded coupon")
            Assert.Contains("(в списке)", line.Value)
        }

    [<Fact>]
    let ``/added shows queue position for a third fiver pushed out by the 2-slot cap`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 98002L, username = "added_status_fiver_queue")
            let other = Tg.user(id = 98003L, username = "added_status_fiver_other")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(other.Id, "member")

            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            do! conn.OpenAsync()
            do! seedUser conn owner.Id "added_status_fiver_queue"
            do! seedUser conn other.Id "added_status_fiver_other"

            // 3 fivers (min_check=25), same owner, same future expiry date, inserted in id order.
            let fiverExpiry = fixture.FixedToday.AddDays(30)
            do! seedCoupon conn owner.Id "as-fq-fiver-1" 5.00m 25.00m fiverExpiry "available" None None
            do! seedCoupon conn owner.Id "as-fq-fiver-2" 5.00m 25.00m fiverExpiry "available" None None
            do! seedCoupon conn owner.Id "as-fq-fiver-3" 5.00m 25.00m fiverExpiry "available" None None

            // 4 non-fiver fillers, owned by someone else (so they don't show up in owner's /added
            // but do occupy the pool's fill-to-6 slots ahead of the 3rd fiver by expiry).
            for i in 1..4 do
                let expiresAt = fixture.FixedToday.AddDays(5 + i)
                do! seedCoupon conn other.Id $"as-fq-filler-{i}" 20.00m 999.00m expiresAt "available" None None

            let! fiver3Id = getCouponIdByPhoto "as-fq-fiver-3"

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage("/added", owner))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            let line = getAddedLineForCoupon calls owner.Id fiver3Id
            Assert.True(line.IsSome, "Expected an /added line for the 3rd fiver coupon")
            Assert.Contains("в очереди: впереди 2", line.Value)
            Assert.Contains("из 25€", line.Value)
            Assert.DoesNotContain("(в списке)", line.Value)
        }

    [<Fact>]
    let ``/added shows начнёт действовать with the formatted valid_from date`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 98004L, username = "added_status_not_yet_valid")
            do! fixture.SetChatMemberStatus(owner.Id, "member")

            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            do! conn.OpenAsync()
            do! seedUser conn owner.Id "added_status_not_yet_valid"

            let validFrom = fixture.FixedToday.AddDays(10)
            let expiresAt = fixture.FixedToday.AddDays(40)
            do! seedCoupon conn owner.Id "as-nyv-1" 10.00m 50.00m expiresAt "available" (Some validFrom) None

            let! couponId = getCouponIdByPhoto "as-nyv-1"

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage("/added", owner))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            let line = getAddedLineForCoupon calls owner.Id couponId
            Assert.True(line.IsSome, "Expected an /added line for the not-yet-valid coupon")
            let expectedDate = validFrom.ToString("d MMMM, dddd", CultureInfo("ru-RU"))
            Assert.Contains("начнёт действовать с", line.Value)
            Assert.Contains(expectedDate, line.Value)
            Assert.DoesNotContain("в списке", line.Value)
            Assert.DoesNotContain("в очереди", line.Value)
        }

    [<Fact>]
    let ``/added still shows verbatim (взят) and (отмечен использованным) suffixes`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 98005L, username = "added_status_taken_reported")
            let taker = Tg.user(id = 98006L, username = "added_status_taker")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            do! conn.OpenAsync()
            do! seedUser conn owner.Id "added_status_taken_reported"
            do! seedUser conn taker.Id "added_status_taker"

            let expiresTaken = fixture.FixedToday.AddDays(10)
            let expiresReported = fixture.FixedToday.AddDays(15)
            do! seedCoupon conn owner.Id "as-tr-taken" 10.00m 50.00m expiresTaken "taken" None (Some taker.Id)
            do! seedCoupon conn owner.Id "as-tr-reported" 10.00m 50.00m expiresReported "reported" None (Some taker.Id)

            let! takenId = getCouponIdByPhoto "as-tr-taken"
            let! reportedId = getCouponIdByPhoto "as-tr-reported"

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage("/added", owner))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            let takenLine = getAddedLineForCoupon calls owner.Id takenId
            let reportedLine = getAddedLineForCoupon calls owner.Id reportedId
            Assert.True(takenLine.IsSome, "Expected an /added line for the taken coupon")
            Assert.True(reportedLine.IsSome, "Expected an /added line for the reported coupon")
            Assert.Contains("(взят)", takenLine.Value)
            Assert.Contains("(отмечен использованным)", reportedLine.Value)
        }

    [<Fact>]
    let ``/added waiting suffix mentions the guaranteed day-of-expiry bound`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 98007L, username = "added_status_waiting_bound")
            let other = Tg.user(id = 98008L, username = "added_status_waiting_bound_other")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(other.Id, "member")

            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            do! conn.OpenAsync()
            do! seedUser conn owner.Id "added_status_waiting_bound"
            do! seedUser conn other.Id "added_status_waiting_bound_other"

            // 3 coupons at min_check=40 (1-slot cap), same owner, same future expiry date:
            // the 2nd and 3rd get pushed to Waiting.
            let mc40Expiry = fixture.FixedToday.AddDays(25)
            do! seedCoupon conn owner.Id "as-wb-mc40-1" 10.00m 40.00m mc40Expiry "available" None None
            do! seedCoupon conn owner.Id "as-wb-mc40-2" 10.00m 40.00m mc40Expiry "available" None None
            do! seedCoupon conn owner.Id "as-wb-mc40-3" 10.00m 40.00m mc40Expiry "available" None None

            // 5 non-fiver fillers owned by someone else, expiring sooner, so they occupy all
            // fill-to-6 slots ahead of the 2nd/3rd mc=40 coupons.
            for i in 1..5 do
                let expiresAt = fixture.FixedToday.AddDays(5 + i)
                do! seedCoupon conn other.Id $"as-wb-filler-{i}" 20.00m 999.00m expiresAt "available" None None

            let! mc40Coupon2Id = getCouponIdByPhoto "as-wb-mc40-2"

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage("/added", owner))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            let line = getAddedLineForCoupon calls owner.Id mc40Coupon2Id
            Assert.True(line.IsSome, "Expected an /added line for the queued mc=40 coupon")
            Assert.Contains("точно будет в списке в день истечения", line.Value)
        }

    [<Fact>]
    let ``/added shows generic queue suffix without "из" for an out-of-catalog min_check`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 98009L, username = "added_status_generic_queue")
            let other = Tg.user(id = 98010L, username = "added_status_generic_queue_other")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(other.Id, "member")

            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            do! conn.OpenAsync()
            do! seedUser conn owner.Id "added_status_generic_queue"
            do! seedUser conn other.Id "added_status_generic_queue_other"

            // Out-of-catalog min_check=15 (no dedicated /list bucket: 25/40/50/100 only),
            // not expiring today, expiring later than all 6 fillers below.
            let targetExpiry = fixture.FixedToday.AddDays(30)
            do! seedCoupon conn owner.Id "as-gq-target" 5.00m 15.00m targetExpiry "available" None None

            // 6 fillers (owned by someone else) expiring sooner, so they fill the list to 6
            // and the out-of-catalog coupon never gets a slot.
            for i in 1..6 do
                let expiresAt = fixture.FixedToday.AddDays(5 + i)
                do! seedCoupon conn other.Id $"as-gq-filler-{i}" 20.00m 999.00m expiresAt "available" None None

            let! targetId = getCouponIdByPhoto "as-gq-target"

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage("/added", owner))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            let line = getAddedLineForCoupon calls owner.Id targetId
            Assert.True(line.IsSome, "Expected an /added line for the out-of-catalog coupon")
            Assert.Contains("(в очереди, точно будет в списке в день истечения)", line.Value)
            Assert.DoesNotContain("впереди", line.Value)
        }
