namespace CouponHubBot.Tests

open System
open System.IO
open System.Net
open System.Text.Json
open BotInfra
open BotTestInfra
open SixLabors.ImageSharp
open SixLabors.ImageSharp.PixelFormats
open Xunit
open ZXing
open FakeCallHelpers

type BarcodeFlowTests(fixture: DefaultCouponHubTestContainers) =
    let owner = Tg.user(id = 98701L, username = "barcode_owner")
    let holder = Tg.user(id = 98702L, username = "barcode_holder")
    let outsider = Tg.user(id = 98703L, username = "barcode_outsider")
    let value = "2000000000015"

    let prepare (barcode: string) =
        task {
            do! fixture.TruncateCoupons()
            for user in [| owner; holder; outsider |] do
                do! fixture.SetChatMemberStatus(user.Id, "member")
            do! fixture.InvalidateMembershipCache()
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner, fileId = "barcode-photo"))
            let! id = fixture.QuerySingle<int>("SELECT id FROM coupon WHERE owner_id = @owner", {| owner = owner.Id |})
            let! _ = fixture.Execute("UPDATE coupon SET barcode_text = @barcode WHERE id = @id", {| id = id; barcode = barcode |})
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback($"take:{id}", holder))
            return id
        }

    [<Fact>]
    member _.``Individual coupon button sends a lossless decodable PNG without changing coupon state`` () =
        task {
            let! id = prepare value
            let! photos = fixture.GetFakeCalls("sendPhoto")
            let photo = Assert.Single photos
            use photoBody = JsonDocument.Parse photo.Body
            let buttons = photoBody.RootElement.GetProperty("reply_markup").GetProperty("inline_keyboard")
            let button =
                buttons.EnumerateArray()
                |> Seq.collect (fun row -> row.EnumerateArray())
                |> Seq.find (fun b -> b.GetProperty("text").GetString() = "Сгенерировать баркод")
            let callback = button.GetProperty("callback_data").GetString()
            Assert.Equal($"barcode:{id}", callback)
            let! eventsBefore = fixture.QuerySingle<int64>("SELECT count(*) FROM coupon_event WHERE coupon_id = @id", {| id = id |})
            do! fixture.ClearFakeCalls()
            let! response = fixture.SendUpdate(Tg.dmCallback(callback, holder))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            let! documents = fixture.GetFakeCalls("sendDocument")
            let document = Assert.Single documents
            use body = JsonDocument.Parse document.Body
            Assert.Equal(holder.Id, body.RootElement.GetProperty("chat_id").GetInt64())
            Assert.Equal($"Баркод купона ID:{id}", body.RootElement.GetProperty("caption").GetString())
            let upload = Assert.Single(body.RootElement.GetProperty("_files").EnumerateArray())
            Assert.Equal($"coupon-{id}.png", upload.GetProperty("fileName").GetString())
            let bytes = Convert.FromBase64String(upload.GetProperty("contentBase64").GetString())
            Assert.Equal<byte>([| 137uy; 80uy; 78uy; 71uy; 13uy; 10uy; 26uy; 10uy |], bytes[..7])
            use stream = new MemoryStream(bytes)
            use image = Image.Load<L8>(stream)
            let reader = ZXing.ImageSharp.BarcodeReader<L8>()
            reader.Options.PossibleFormats <- [| BarcodeFormat.EAN_13 |]
            Assert.Equal(value, reader.Decode(image).Text)
            let! eventsAfter = fixture.QuerySingle<int64>("SELECT count(*) FROM coupon_event WHERE coupon_id = @id", {| id = id |})
            Assert.Equal(eventsBefore, eventsAfter)
            let! status = fixture.QuerySingle<string>("SELECT status FROM coupon WHERE id = @id", {| id = id |})
            Assert.Equal("taken", status)
        }

    [<Theory>]
    [<InlineData("used")>]
    [<InlineData("voided")>]
    member _.``Generation does not enforce coupon validity`` (status: string) =
        task {
            let! id = prepare value
            let! _ = fixture.Execute("UPDATE coupon SET status = @status, expires_at = '2000-01-01' WHERE id = @id", {| id = id; status = status |})
            do! fixture.ClearFakeCalls()
            let! response = fixture.SendUpdate(Tg.dmCallback($"barcode:{id}", holder))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            let! documents = fixture.GetFakeCalls("sendDocument")
            %Assert.Single documents
        }

    [<Theory>]
    [<InlineData(null)>]
    [<InlineData("2000000000014")>]
    member _.``Missing or malformed stored numbers produce a useful response`` (barcode: string) =
        task {
            let! id = prepare barcode
            do! fixture.ClearFakeCalls()
            let! response = fixture.SendUpdate(Tg.dmCallback($"barcode:{id}", holder))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            let! documents = fixture.GetFakeCalls("sendDocument")
            Assert.Empty documents
            let! messages = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText messages holder.Id "Не удалось сгенерировать баркод")
        }

    [<Fact>]
    member _.``Crafted callback cannot expose another members coupon`` () =
        task {
            let! id = prepare value
            do! fixture.ClearFakeCalls()
            let! response = fixture.SendUpdate(Tg.dmCallback($"barcode:{id}", outsider))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            let! documents = fixture.GetFakeCalls("sendDocument")
            Assert.Empty documents
            let! messages = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText messages outsider.Id "Купон не найден или недоступен")
        }

    [<Fact>]
    member _.``Membership is checked when generating an image`` () =
        task {
            let! id = prepare value
            do! fixture.SetChatMemberStatus(holder.Id, "left")
            do! fixture.InvalidateMembershipCache()
            do! fixture.ClearFakeCalls()
            let! response = fixture.SendUpdate(Tg.dmCallback($"barcode:{id}", holder))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            let! documents = fixture.GetFakeCalls("sendDocument")
            Assert.Empty documents
        }

    [<Theory>]
    [<InlineData("barcode:foo")>]
    [<InlineData("barcode:-1")>]
    [<InlineData("barcode:1:extra")>]
    [<InlineData("barcode:999999999999999999")>]
    member _.``Malformed callbacks are handled without an upload`` (callback: string) =
        task {
            do! fixture.SetChatMemberStatus(holder.Id, "member")
            do! fixture.InvalidateMembershipCache()
            do! fixture.ClearFakeCalls()
            let! response = fixture.SendUpdate(Tg.dmCallback(callback, holder))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            let! documents = fixture.GetFakeCalls("sendDocument")
            Assert.Empty documents
            let! messages = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText messages holder.Id "Не удалось определить купон")
        }
