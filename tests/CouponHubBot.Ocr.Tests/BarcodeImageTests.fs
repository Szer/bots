namespace CouponHubBot.Ocr.Tests

open System.IO
open BotInfra
open CouponHubBot.Services
open CouponHubBot.Utils
open SixLabors.ImageSharp
open SixLabors.ImageSharp.PixelFormats
open SixLabors.ImageSharp.Processing
open Xunit
open ZXing

type BarcodeImageTests() =
    [<Theory>]
    [<InlineData("2000000000015")>]
    [<InlineData("0000000000000")>]
    [<InlineData("1234567890128")>]
    member _.``PNG remains readable at phone display sizes`` (value: string) =
        let bytes = CouponBarcodeImage.tryRender value |> Option.get
        use input = new MemoryStream(bytes)
        use image = Image.Load<L8>(input)
        let reader = ZXing.ImageSharp.BarcodeReader<L8>()
        reader.Options.PossibleFormats <- [| BarcodeFormat.EAN_13 |]
        for width in [| image.Width; 516; 387 |] do
            use resized = image.Clone(fun ctx -> %ctx.Resize(width, 0))
            let result = reader.Decode resized
            Assert.NotNull result
            Assert.Equal(value, result.Text)

    [<Theory>]
    [<InlineData(null)>]
    [<InlineData("")>]
    [<InlineData("200000000001")>]
    [<InlineData("2000000000014")>]
    [<InlineData("20000000000150")>]
    [<InlineData("20000000000A5")>]
    [<InlineData("２００００００００００１５")>]
    member _.``Invalid values are never repaired or encoded`` (value: string) =
        Assert.True((CouponBarcodeImage.tryRender value).IsNone)
