namespace CouponHubBot.Services

open System
open System.IO
open SixLabors.ImageSharp
open SixLabors.ImageSharp.PixelFormats

module CouponBarcodeReader =
    let tryDecode (imageBytes: ReadOnlyMemory<byte>) =
        // Resolve the library before constructing objects with native finalizers.
        let format = ZXingCpp.BarcodeFormat.Parse("EAN13")
        use ms = new MemoryStream(imageBytes.ToArray())
        use image = Image.Load<L8>(ms)
        // Checked crop views require a full trailing row stride, including unused pixels.
        let pixels = Array.zeroCreate<byte> (image.Width * (image.Height + 1))
        image.CopyPixelDataTo(pixels)
        use reader = new ZXingCpp.BarcodeReader()
        reader.Formats <- ZXingCpp.BarcodeFormats(format)
        reader.TryHarder <- true
        reader.TryRotate <- true
        reader.TryInvert <- false
        reader.TryDownscale <- true
        reader.MaxNumberOfSymbols <- 1
        reader.MinLineCount <- 2
        reader.ReturnErrors <- false
        // ImageView borrows the pixel buffer throughout native decoding.
        use _pin = Memory<byte>(pixels).Pin()
        let decode x y width height =
            let offset = y * image.Width + x
            let length = height * image.Width
            let region = ReadOnlySpan<byte>(pixels, offset, length)
            let view = ZXingCpp.ImageView(region, width, height, ZXingCpp.ImageFormat.Lum, image.Width)
            let results =
                try reader.From(view)
                finally GC.KeepAlive(view)
            try
                results
                |> Array.tryPick (fun barcode ->
                    if barcode.IsValid && not (String.IsNullOrWhiteSpace barcode.Text) then
                        Some barcode.Text
                    else
                        None)
            finally
                for barcode in results do
                    barcode.Dispose()

        let result =
            match decode 0 0 image.Width image.Height with
            | Some text -> Some text
            | None ->
                let bands = [| 0.0, 0.50; 0.0, 0.60; 0.20, 0.70; 0.30, 0.80; 0.45, 1.0; 0.55, 1.0; 0.65, 1.0 |]
                [| 0.0; 0.10; 0.20 |]
                |> Array.tryPick (fun margin ->
                    bands
                    |> Array.tryPick (fun (top, bottom) ->
                        let x = int (Math.Round(float image.Width * margin))
                        let y = min (image.Height - 1) (int (Math.Round(float image.Height * top)))
                        let width = image.Width - 2 * x
                        let height = max 1 (int (Math.Round(float image.Height * bottom)) - y)
                        decode x y width height))
        result
