namespace CouponHubBot.Services

open System
open System.Collections.Generic
open ZXing
open ZXing.Common
open ZXing.OneD

module internal BarcodePrintStreak =
    type private Hit =
        { X: float
          Y: int
          Width: float
          Text: string }

    let private scanRow (reader: MultiFormatUPCEANReader) hints (row: byte[]) y =
        let edges = ResizeArray<int>()
        let threshold = (int (Array.min row) + int (Array.max row)) / 2
        let mutable dark = false
        for x in 0 .. row.Length - 1 do
            let next = int row[x] < threshold
            if next <> dark then
                edges.Add x
                dark <- next
        if dark then edges.Add row.Length
        // An EAN-13 has 59 alternating runs across 95 modules. A split black bar adds two runs.
        [| for start in 0 .. 2 .. edges.Count - 62 do
               let edge i = edges[start + i]
               let run i = edge (i + 1) - edge i
               let pitch = float (edge 61 - edge 0) / 95.0
               let left = if start = 0 then 0 else edges[start - 1]
               let right = if start + 62 = edges.Count then row.Length else edge 62
               if pitch >= 2.5 && float (edge 0 - left) >= 3.0 * pitch && float (right - edge 61) >= 3.0 * pitch then
                   for gap in 1 .. 2 .. 59 do
                       let width = float (run gap)
                       if width <= 1.5 * pitch && float (min (run (gap - 1)) (run (gap + 1))) < 0.65 * pitch then
                           let quiet = min (int (ceil (8.0 * pitch))) (min (edge 0 - left) (right - edge 61))
                           let bits = BitArray(edge 61 - edge 0 + 2 * quiet)
                           let offset = quiet - edge 0
                           for i in 0 .. 2 .. 60 do bits.setRange(edge i + offset, edge (i + 1) + offset)
                           bits.setRange(edge gap + offset, edge (gap + 1) + offset)
                           let result = reader.decodeRow(y, bits, hints)
                           if not (isNull result) && result.BarcodeFormat = BarcodeFormat.EAN_13 then
                               yield { X = float (edge gap) + width / 2.0; Y = y; Width = width; Text = result.Text } |]

    // Repeated bars can mimic split strokes. Exclude rows with many edges around the projected streak.
    let private looksLikeText (pixels: byte[]) center distance =
        let radius = 20 * distance
        let left = max 0 (center - radius)
        let right = min (pixels.Length - 1) (center + radius)
        let threshold = int pixels[center] - 45
        let mutable edges = 0
        for x in left + 1 .. right do
            if (int pixels[x] < threshold) <> (int pixels[x - 1] < threshold) then edges <- edges + 1
        edges < 15

    let private hasSplitPrint (image: LuminanceSource) (hits: Hit[]) =
        let first, last = Array.head hits, Array.last hits
        let height = last.Y - first.Y
        let mean f = Array.averageBy f hits
        let midY, midX = mean (fun h -> float h.Y), mean (fun h -> h.X)
        let covariance = mean (fun h -> (float h.Y - midY) * (h.X - midX))
        let slope = covariance / mean (fun h -> pown (float h.Y - midY) 2)
        let xAt y = midX + slope * (float y - midY)
        let width = mean (fun h -> h.Width)
        let followsLine = hits |> Array.forall (fun h -> abs (h.X - xAt h.Y) <= max 2.0 (width * 0.5))
        if not followsLine then false
        else
            let row = Array.zeroCreate<byte> image.Width
            let mutable bands = 0
            let mutable lastInk = -image.Height
            let step = max 1 (image.Height / 600)
            let distance = max 2 (int (ceil (width * 0.8)))
            for y in max 0 (first.Y - 8 * height) .. step .. min (image.Height - 1) (last.Y + 8 * height) do
                if y < first.Y - height / 2 || y > last.Y + height / 2 then
                    let x = int (round (xAt y))
                    let tolerance = max 1 (int (width / 2.0))
                    if x >= distance + tolerance && x < image.Width - distance - tolerance then
                        let pixels = image.getRow(y, row)
                        let splitAt center =
                            int pixels[center] - max (int pixels[center - distance]) (int pixels[center + distance]) >= 45
                        let acrossPrint center = splitAt center && looksLikeText pixels center distance
                        if seq { x - tolerance .. x + tolerance } |> Seq.exists acrossPrint then
                            if y - lastInk >= max (3 * step) (height / 12) then bands <- bands + 1
                            lastInk <- y
            bands >= 2

    let tryDecode (image: LuminanceSource) =
        let step = max 1 (image.Height / 200)
        let row = Array.zeroCreate<byte> image.Width
        let hints = Dictionary<DecodeHintType, obj>()
        hints.Add(DecodeHintType.POSSIBLE_FORMATS, [| BarcodeFormat.EAN_13 |])
        let reader = MultiFormatUPCEANReader(hints)
        let hits = [| for y in 0 .. step .. image.Height - 1 do yield! scanRow reader hints (image.getRow(y, row)) y |]
        let candidates =
            hits
            |> Array.groupBy (fun h -> h.Text)
            |> Array.choose (fun (text, observations) ->
                let rows = observations |> Array.distinctBy (fun h -> h.Y)
                let enoughRows = rows.Length >= 4 && rows[rows.Length - 1].Y - rows[0].Y >= 4 * step
                if enoughRows && hasSplitPrint image rows then Some text else None)
        match candidates with
        | [| value |] -> Some value
        | _ -> None
