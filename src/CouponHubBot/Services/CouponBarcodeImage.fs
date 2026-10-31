namespace CouponHubBot.Services

open System
open System.IO
open SixLabors.ImageSharp
open SixLabors.ImageSharp.PixelFormats
open ZXing
open ZXing.OneD

module CouponBarcodeImage =
    let private digits =
        [| [| "01110"; "10001"; "10011"; "10101"; "11001"; "10001"; "01110" |]
           [| "00100"; "01100"; "00100"; "00100"; "00100"; "00100"; "01110" |]
           [| "01110"; "10001"; "00001"; "00010"; "00100"; "01000"; "11111" |]
           [| "11110"; "00001"; "00001"; "01110"; "00001"; "00001"; "11110" |]
           [| "00010"; "00110"; "01010"; "10010"; "11111"; "00010"; "00010" |]
           [| "11111"; "10000"; "10000"; "11110"; "00001"; "00001"; "11110" |]
           [| "01110"; "10000"; "10000"; "11110"; "10001"; "10001"; "01110" |]
           [| "11111"; "00001"; "00010"; "00100"; "01000"; "01000"; "01000" |]
           [| "01110"; "10001"; "10001"; "01110"; "10001"; "10001"; "01110" |]
           [| "01110"; "10001"; "10001"; "01111"; "00001"; "00001"; "01110" |] |]

    let tryRender (value: string | null) =
        if isNull value || value.Length <> 13
           || not (value |> Seq.forall (fun c -> c >= '0' && c <= '9'))
           || (value |> Seq.mapi (fun i c -> (int c - int '0') * (if i % 2 = 0 then 1 else 3)) |> Seq.sum) % 10 <> 0 then
            None
        else
            let modules = EAN13Writer().encode value
            let scale = 8
            let padding = 8
            let left = padding + 11
            let barHeight = 70
            use image = new Image<L8>((left + modules.Length + 7 + padding) * scale, (padding + barHeight + 9 + padding) * scale, L8(255uy))
            let rectangle x y width height =
                for py in y * scale .. (y + height) * scale - 1 do
                    for px in x * scale .. (x + width) * scale - 1 do
                        image[px, py] <- L8(0uy)

            // EAN-13 guards extend five modules below the bars; quiet zones remain white.
            for i in 0 .. modules.Length - 1 do
                if modules[i] then
                    let guard = i < 3 || (i >= 45 && i < 50) || i >= 92
                    rectangle (left + i) padding 1 (barHeight + if guard then 5 else 0)

            for i in 0 .. value.Length - 1 do
                let x =
                    match i with
                    | 0 -> left - 9
                    | n when n <= 6 -> left + 4 + (n - 1) * 7
                    | n -> left + 51 + (n - 7) * 7
                let glyph = digits[int value[i] - int '0']
                for row in 0 .. 6 do
                    for col in 0 .. 4 do
                        if glyph[row][col] = '1' then
                            rectangle (x + col) (padding + barHeight + 2 + row) 1 1

            let reader = ZXing.ImageSharp.BarcodeReader<L8>()
            reader.Options.PossibleFormats <- [| BarcodeFormat.EAN_13 |]
            let decoded = reader.Decode image
            if isNull decoded || decoded.Text <> value then
                invalidOp "Generated EAN-13 image failed its decode check."
            use output = new MemoryStream()
            image.SaveAsPng output
            Some(output.ToArray())
