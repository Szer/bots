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

    let private corroboratesMiddle (view: LuminanceSource) y reverse (points: ResizeArray<ResultPoint>) pitch candidate =
        let reader = EAN13Reader()
        let left, right = int points[0].X, int points[2].X
        let step = max 1 (int pitch)
        let pixels = Array.zeroCreate<byte> view.Width
        let bits = BitArray(view.Width)
        let mutable votes = 0
        let mutable conflict = false
        for offset in -15 .. 15 do
            let otherY = y + offset * step
            if offset <> 0 && otherY >= 0 && otherY < view.Height then
                let scan = view.getRow(otherY, pixels)
                let pixel x = scan[if reverse then view.Width - 1 - x else x]
                let region = [| for x in left .. right -> pixel x |]
                let threshold = (int (Array.min region) + int (Array.max region)) / 2
                let dark x = int (pixel x) < threshold
                for x in 1 .. view.Width - 2 do
                    let a, b, c = dark (x - 1), dark x, dark (x + 1)
                    bits[x] <- (a && b) || (a && c) || (b && c)
                let read = reader.decodeRow(otherY, bits, null)
                if not (isNull read) && abs (read.ResultPoints[0].X - points[0].X) <= 2.0f * pitch
                   && abs (read.ResultPoints[1].X - points[2].X) <= 2.0f * pitch then
                    let l, r = int read.ResultPoints[0].X, int read.ResultPoints[1].X
                    let mutable transitions = 0
                    for x in l + 1 .. r do
                        if bits[x] <> bits[x - 1] then transitions <- transitions + 1
                    // Both guard midpoints lie in their central spaces on a clean, uniformly spaced row.
                    if transitions = 56 then
                        if read.Text = candidate then votes <- votes + 1 else conflict <- true
        votes >= 4 && not conflict

    let private tryRepairMiddle view y reverse (row: BitArray) (points: ResizeArray<ResultPoint>) =
        let pitch = (points[2].X - points[0].X) / 92.0f
        let start = max 1 (int (points[0].X - 4.0f * pitch))
        let dataEnd = int points[1].X
        let edges = [| for x in start .. dataEnd do if row[x] <> row[x - 1] then yield x |]
        // Data ends 56 runs after the start guard unless the middle-guard search skipped runs.
        let reader = EAN13Reader()
        let candidates = seq {
            for extra in [2; 4] do
                let first = edges.Length - 57 - extra
                if first >= 0 && edges[edges.Length - 1] = dataEnd && row[edges[first]]
                   && float32 (edges[first] + edges[first + 3]) / 2.0f = points[0].X then
                    let narrow = [| for i in first + 3 .. first + 26 + extra do
                                        if float32 (edges[i + 1] - edges[i]) <= 0.5f * pitch then yield i |]
                    let edits = seq {
                        for i in narrow do
                            if extra = 2 then yield [i]
                            else
                                for j in narrow do
                                    if j > i + 1 then yield [i; j] }
                    for runs in edits do
                        let repaired = row.Clone() :?> BitArray
                        for i in runs do
                            for x in edges[i] .. edges[i + 1] - 1 do repaired.flip(x)
                        let read = reader.decodeRow(y, repaired, null)
                        if not (isNull read) && read.ResultPoints[0].X = points[0].X
                           && read.ResultPoints[1].X = points[2].X then yield read.Text }
        match candidates |> Seq.distinct |> Seq.truncate 2 |> Seq.toList with
        | [text] when corroboratesMiddle view y reverse points pitch text -> Some text
        | _ -> None

    let tryDecodeSkippedRuns (img: LuminanceSource) (result: Result) =
        let first, last = result.ResultPoints[0], result.ResultPoints[1]
        let vertical = first.X = last.X && first.Y <> last.Y
        let view = if vertical then img.rotateCounterClockwise() else img
        let y, firstX, lastX =
            if vertical then img.Width - 1 - int first.X, first.Y, last.Y
            else int first.Y, first.X, last.X
        let row = BarcodeBinarizer(view).getBlackRow(y, null)
        let reverse = firstX > lastX
        if reverse then row.reverse()
        let points = ResizeArray<ResultPoint>()
        let hints = Dictionary<DecodeHintType, obj>()
        hints.Add(DecodeHintType.NEED_RESULT_POINT_CALLBACK, ResultPointCallback(points.Add))
        let replay = EAN13Reader().decodeRow(y, row, hints)
        let xOf x = if reverse then float32 (row.Size - 1) - x else x
        if isNull replay || replay.Text <> result.Text || points.Count <> 3
           || points[0].X <> xOf firstX || points[2].X <> xOf lastX then None
        else
            // ZXing reports the end of the digits and the midpoint of the end guard separately.
            let dataEnd = int points[1].X
            let afterPair x = row.getNextSet(row.getNextUnset(x))
            let guardStart = afterPair dataEnd
            let guardEnd = row.getNextUnset(afterPair guardStart)
            let immediateEnd = row.getNextUnset(afterPair dataEnd)
            if float32 (dataEnd + immediateEnd) / 2.0f = points[2].X then
                tryRepairMiddle view y reverse row points
            elif dataEnd >= row.Size || not row[dataEnd] || guardEnd >= row.Size
               || float32 (guardStart + guardEnd) / 2.0f <> points[2].X then None
            else
                let confirmedOnRow text =
                    let reader = EAN13Reader()
                    let mutable gap = row.getNextUnset(int points[0].X)
                    let mutable confirmed = false
                    while not confirmed && gap < dataEnd do
                        let stop = row.getNextSet(gap)
                        let filled = row.Clone() :?> BitArray
                        filled.setRange(gap, stop)
                        let recovered = reader.decodeRow(y, filled, null)
                        confirmed <-
                            not (isNull recovered) && recovered.Text = text
                            && recovered.ResultPoints[0].X = points[0].X
                            && recovered.ResultPoints[1].X = points[2].X
                        gap <- row.getNextUnset(stop)
                    confirmed
                tryDecode view |> Option.filter confirmedOnRow
