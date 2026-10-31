namespace CouponHubBot.Services

open System
open System.Globalization
open System.IO
open System.Numerics
open BotInfra
open CouponHubBot
open SixLabors.Fonts
open SixLabors.ImageSharp
open SixLabors.ImageSharp.Drawing.Processing
open SixLabors.ImageSharp.PixelFormats
open SixLabors.ImageSharp.Processing
open ZXing
open ZXing.OneD

module CouponBarcodeImage =
    let private fonts =
        lazy
            let collection = FontCollection()
            let load name =
                use stream = typeof<Coupon>.Assembly.GetManifestResourceStream($"CouponHubBot.Fonts.{name}.ttf")
                collection.Add stream
            let family = load "Regular"
            %load "Bold"
            family

    let private money (value: decimal) = value.ToString("0.##", CultureInfo.InvariantCulture)

    let formatValidity (validFrom: DateOnly option) (expiresAt: DateOnly) =
        let date (value: DateOnly) format = value.ToString(format, CultureInfo.InvariantCulture)
        let finish = date expiresAt "d MMM yyyy"
        match validFrom with
        | Some start ->
            let format = if start.Year = expiresAt.Year then "d MMM" else "d MMM yyyy"
            $"Valid {date start format} – {finish}"
        | None -> $"Valid until {finish}"

    let tryRender (barcode: string | null) (value: decimal) (minimum: decimal) (validFrom: DateOnly option) (expiresAt: DateOnly) =
        if isNull barcode || barcode.Length <> 13
           || not (barcode |> Seq.forall (fun c -> c >= '0' && c <= '9'))
           || (barcode |> Seq.mapi (fun i c -> (int c - int '0') * (if i % 2 = 0 then 1 else 3)) |> Seq.sum) % 10 <> 0 then
            None
        else
            let modules = EAN13Writer().encode barcode
            let scale = 8
            let left = 160
            let top = 580
            let barHeight = 70 * scale
            use image = new Image<L8>(1080, 1400, L8(255uy))
            // Quiet zones surround the bars; guards extend five modules below them.
            for i in 0 .. modules.Length - 1 do
                if modules[i] then
                    let guard = i < 3 || (i >= 45 && i < 50) || i >= 92
                    let height = barHeight + if guard then 5 * scale else 0
                    for y in top .. top + height - 1 do
                        for x in left + i * scale .. left + (i + 1) * scale - 1 do
                            image[x, y] <- L8(0uy)

            let family = fonts.Value
            image.Mutate(fun ctx ->
                %ctx.Paint(fun canvas ->
                    let text (style: FontStyle) size x y alignment color maxWidth (content: string) =
                        let mutable font = family.CreateFont(size, style)
                        let width = TextMeasurer.MeasureAdvance(content, TextOptions(font)).Width
                        if width > maxWidth then font <- family.CreateFont(size * maxWidth / width, style)
                        let options = RichTextOptions(font)
                        options.Origin <- Vector2(x, y)
                        options.HorizontalAlignment <- alignment
                        canvas.DrawText(options, content, Brushes.Solid(color), null)
                    let centered style size y color content =
                        text style size 540.f y HorizontalAlignment.Center color 940.f content
                    text FontStyle.Bold 50.f 70.f 60.f HorizontalAlignment.Left Color.Black 300.f "DUNNES"
                    text FontStyle.Regular 28.f 297.f 112.f HorizontalAlignment.Right Color.Black 200.f "STORES"
                    centered FontStyle.Bold 64.f 230.f Color.Black $"€{money value} OFF €{money minimum}"
                    centered FontStyle.Regular 42.f 350.f Color.Black (formatValidity validFrom expiresAt)
                    centered FontStyle.Regular 36.f 460.f Color.DimGray $"When you spend €{money minimum} or more on Groceries."
                    centered FontStyle.Regular 44.f 1210.f Color.Black $"{barcode[..6]} {barcode[7..10]} {barcode[11..]}"
                    centered FontStyle.Regular 34.f 1320.f Color.DimGray "Terms and conditions apply."))

            let reader = ZXing.ImageSharp.BarcodeReader<L8>()
            reader.Options.PossibleFormats <- [| BarcodeFormat.EAN_13 |]
            let decoded = reader.Decode image
            if isNull decoded || decoded.Text <> barcode then
                invalidOp "Generated EAN-13 image failed its decode check."
            use output = new MemoryStream()
            image.SaveAsPng output
            Some(output.ToArray())
