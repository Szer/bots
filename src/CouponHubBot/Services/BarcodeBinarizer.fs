namespace CouponHubBot.Services

open System
open System.Runtime.InteropServices
open System.Runtime.Intrinsics
open ZXing
open ZXing.Common

type internal BarcodeBinarizer(source: LuminanceSource) =
    inherit GlobalHistogramBinarizer(source)

    let mutable luminances = Array.empty<byte>
    let buckets = Array.zeroCreate<int> 32

    // Histogram and valley selection follow ZXing.Net; see ThirdPartyNotices.txt.
    let estimateThreshold () =
        let mutable firstPeak = 0
        let mutable firstPeakSize = 0
        let mutable maxBucketCount = 0
        for x = 0 to buckets.Length - 1 do
            if buckets[x] > firstPeakSize then
                firstPeak <- x
                firstPeakSize <- buckets[x]
            maxBucketCount <- max maxBucketCount buckets[x]

        let mutable secondPeak = 0
        let mutable secondPeakScore = 0
        for x = 0 to buckets.Length - 1 do
            let distance = x - firstPeak
            let score = buckets[x] * distance * distance
            if score > secondPeakScore then
                secondPeak <- x
                secondPeakScore <- score

        let lowPeak = min firstPeak secondPeak
        let highPeak = max firstPeak secondPeak
        if highPeak - lowPeak <= (buckets.Length >>> 4) then
            None
        else
            let mutable valley = highPeak - 1
            let mutable valleyScore = -1
            for x = highPeak - 1 downto lowPeak + 1 do
                let distance = x - lowPeak
                let score = distance * distance * (highPeak - x) * (maxBucketCount - buckets[x])
                if score > valleyScore then
                    valley <- x
                    valleyScore <- score
            Some (valley <<< 3)

    override _.createBinarizer(nextSource) = BarcodeBinarizer(nextSource)

    override _.getBlackRow(y, row) =
        let width = source.Width
        let result =
            if isNull row || row.Size < width then
                BitArray(width)
            else
                row.clear()
                row
        if luminances.Length < width then
            luminances <- Array.zeroCreate width
        let pixels = source.getRow(y, luminances)
        Array.Clear buckets
        for x = 0 to width - 1 do
            let bucket = int pixels[x] >>> 3
            buckets[bucket] <- buckets[bucket] + 1

        match estimateThreshold () with
        | None -> null
        | Some threshold ->
            match width with
            | 1 | 2 ->
                for x = 0 to width - 1 do
                    if int pixels[x] < threshold then
                        result[x] <- true
            | _ ->
                let mutable x = 1
                if Vector128.IsHardwareAccelerated then
                    // Comparing before division preserves truncation toward zero at threshold zero.
                    let bound = Vector128.Create(int16 (if threshold = 0 then -1 else threshold * 2))
                    while x + 16 <= width - 1 do
                        let left = MemoryMarshal.Read<Vector128<byte>>(ReadOnlySpan<byte>(pixels, x - 1, 16))
                        let center = MemoryMarshal.Read<Vector128<byte>>(ReadOnlySpan<byte>(pixels, x, 16))
                        let right = MemoryMarshal.Read<Vector128<byte>>(ReadOnlySpan<byte>(pixels, x + 1, 16))
                        let low =
                            Vector128.ShiftLeft(Vector128.WidenLower(center).AsInt16(), 2)
                            - Vector128.WidenLower(left).AsInt16() - Vector128.WidenLower(right).AsInt16()
                        let high =
                            Vector128.ShiftLeft(Vector128.WidenUpper(center).AsInt16(), 2)
                            - Vector128.WidenUpper(left).AsInt16() - Vector128.WidenUpper(right).AsInt16()
                        let bits =
                            Vector128.Narrow(
                                Vector128.LessThan(low, bound).AsUInt16(),
                                Vector128.LessThan(high, bound).AsUInt16())
                        let mask = Vector128.ExtractMostSignificantBits bits
                        let word = x >>> 5
                        let shift = x &&& 31
                        result.Array[word] <- result.Array[word] ||| int (mask <<< shift)
                        if shift > 16 then
                            result.Array[word + 1] <- result.Array[word + 1] ||| int (mask >>> (32 - shift))
                        x <- x + 16
                while x < width - 1 do
                    let sharpened = (4 * int pixels[x] - int pixels[x - 1] - int pixels[x + 1]) / 2
                    if sharpened < threshold then
                        result[x] <- true
                    x <- x + 1
            result
