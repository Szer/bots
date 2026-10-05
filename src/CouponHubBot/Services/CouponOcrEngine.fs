namespace CouponHubBot.Services

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open CouponHubBot
open CouponHubBot.Telemetry
open CouponHubBot.Utils
open BotInfra

open SixLabors.ImageSharp
open SixLabors.ImageSharp.PixelFormats
open SixLabors.ImageSharp.Processing
open ZXing
open ZXing.Common
open ZXing.ImageSharp

module private CouponOcrParsing =
    let parseDecimalInvariant (s: string) =
        let s2 = s.Trim().Replace(',', '.')
        match Decimal.TryParse(s2, NumberStyles.Number, CultureInfo.InvariantCulture) with
        | true, v -> Some v
        | _ -> None

    let private monthNumber (raw: string) =
        match raw.Trim().ToLowerInvariant() with
        | "jan" | "january" -> Some 1
        | "feb" | "february" -> Some 2
        | "mar" | "march" -> Some 3
        | "apr" | "april" -> Some 4
        | "may" -> Some 5
        | "jun" | "june" -> Some 6
        | "jul" | "july" -> Some 7
        | "aug" | "august" -> Some 8
        | "sep" | "sept" | "september" -> Some 9
        | "oct" | "october" -> Some 10
        | "nov" | "november" -> Some 11
        | "dec" | "december" -> Some 12
        | _ -> None

    let tryParseDateAny (nowUtc: DateTime) (s: string) =
        let styles = DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
        let culture = CultureInfo.InvariantCulture

        // 1) Full dates with explicit year (preferred).
        let formatsWithYear =
            [| "yyyy-MM-dd"
               "yyyy.MM.dd"
               "yyyy/MM/dd"
               "dd.MM.yyyy"
               "d.M.yyyy"
               "dd/MM/yyyy"
               "d/M/yyyy"
               "dd-MM-yyyy"
               "d-M-yyyy"
               "dd.MM.yy"
               "dd/MM/yy"
               "dd-MM-yy"
               "d.M.yy"
               "d/M/yy"
               "d-M-yy" |]

        let mutable parsed = Unchecked.defaultof<DateTime>
        if DateTime.TryParseExact(s, formatsWithYear, culture, styles, &parsed) then
            Some parsed
        else
            // 2) Month/day without year (used by app screenshots).
            // We intentionally interpret as MM-dd / MM/dd to match Dunnes app UI.
            let formatsNoYear = [| "MM-dd"; "M-d"; "MM/dd"; "M/d" |]
            let mutable md = Unchecked.defaultof<DateTime>
            if DateTime.TryParseExact(s, formatsNoYear, culture, DateTimeStyles.None, &md) then
                Some (DateTime(nowUtc.Year, md.Month, md.Day, 0, 0, 0, DateTimeKind.Utc))
            else
                // 3) Month name without year (e.g. "15 Jan", "Jan 15").
                let m1 = Regex.Match(s, @"(?i)\b(?<day>\d{1,2})\s*(?<mon>jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t)?(?:ember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\b")
                let m2 = Regex.Match(s, @"(?i)\b(?<mon>jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t)?(?:ember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\s*(?<day>\d{1,2})\b")
                let m = if m1.Success then m1 else m2
                if m.Success then
                    match Int32.TryParse(m.Groups.["day"].Value), monthNumber m.Groups.["mon"].Value with
                    | (true, d), Some mo ->
                        try Some (DateTime(nowUtc.Year, mo, d, 0, 0, 0, DateTimeKind.Utc))
                        with _ -> None
                    | _ -> None
                else
                    None

    let private monthNameRegex =
        @"jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t)?(?:ember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?"

    let tryParseValidRange (nowUtc: DateTime) (text: string) =
        if String.IsNullOrWhiteSpace text then
            None
        else
            // Common screenshot layout: "Valid 14 Jan - 23 Jan"
            let m1 =
                Regex.Match(
                    text,
                    $@"(?i)\bvalid\s+(?<a>\d{{1,2}}\s*(?:{monthNameRegex}))\s*[-–]\s*(?<b>\d{{1,2}}\s*(?:{monthNameRegex}))\b"
                )

            if m1.Success then
                match tryParseDateAny nowUtc m1.Groups.["a"].Value, tryParseDateAny nowUtc m1.Groups.["b"].Value with
                | Some a, Some b -> Some(a.Date, b.Date)
                | _ -> None
            else
                // Text coupon layout: "Coupon valid from 11/01/26 to 20/01/26"
                let m2 =
                    Regex.Match(
                        text,
                        @"(?i)\bvalid\s+from\s+(?<a>\d{1,4}[./-]\d{1,2}[./-]\d{1,4})\s+to\s+(?<b>\d{1,4}[./-]\d{1,2}[./-]\d{1,4})\b"
                    )
                if m2.Success then
                    match tryParseDateAny nowUtc m2.Groups.["a"].Value, tryParseDateAny nowUtc m2.Groups.["b"].Value with
                    | Some a, Some b -> Some(a.Date, b.Date)
                    | _ -> None
                else
                    None

    let findEuroAmounts (text: string) =
        if String.IsNullOrWhiteSpace text then
            [||]
        else
            // Match both "10€"/"10 EUR" and "€10"/"EUR 10" shapes.
            Regex.Matches(text, @"(?i)(?:€\s*(?<n>\d{1,3}(?:[.,]\d{1,2})?)|(?<n>\d{1,3}(?:[.,]\d{1,2})?)\s*(€|eur)\b|eur\s*(?<n>\d{1,3}(?:[.,]\d{1,2})?))")
            |> Seq.cast<Match>
            |> Seq.choose (fun m -> parseDecimalInvariant m.Groups.["n"].Value)
            |> Seq.distinct
            |> Seq.toArray

    /// Try parse explicit "€10 OFF €50" (or "SAVE €10 ... spend €50") patterns to avoid
    /// picking unrelated amounts from wallet screenshots.
    let tryParseDiscountAndThreshold (text: string) =
        if String.IsNullOrWhiteSpace text then
            None
        else
            // Most reliable: "€10 OFF €50" (also "€10 OFF €50 or more")
            let mOff =
                Regex.Match(
                    text,
                    @"(?i)€\s*(?<v>\d{1,3}(?:[.,]\d{1,2})?)\s*(off|save)\s*€\s*(?<mc>\d{1,3}(?:[.,]\d{1,2})?)"
                )

            if mOff.Success then
                match parseDecimalInvariant mOff.Groups.["v"].Value, parseDecimalInvariant mOff.Groups.["mc"].Value with
                | Some v, Some mc -> Some(v, mc)
                | _ -> None
            else
                // Fallback for some layouts:
                // "SAVE €10" + "When you spend €50"
                let mSave = Regex.Match(text, @"(?i)\b(save|off)\s*€\s*(?<v>\d{1,3}(?:[.,]\d{1,2})?)\b")
                let mSpend = Regex.Match(text, @"(?i)\b(spend|when you spend)\s*€\s*(?<mc>\d{1,3}(?:[.,]\d{1,2})?)\b")
                if mSave.Success && mSpend.Success then
                    match parseDecimalInvariant mSave.Groups.["v"].Value, parseDecimalInvariant mSpend.Groups.["mc"].Value with
                    | Some v, Some mc -> Some(v, mc)
                    | _ -> None
                else
                    None

    let findDates (nowUtc: DateTime) (text: string) =
        if String.IsNullOrWhiteSpace text then
            [||]
        else
            let numericDates =
                // 1) Full numeric date (with year): 2026-01-11, 11/01/26, 11.01.2026
                Regex.Matches(text, @"(?<d>\d{1,4})[./-](?<m>\d{1,2})[./-](?<y>\d{1,4})")
                |> Seq.cast<Match>
                |> Seq.choose (fun m -> tryParseDateAny nowUtc m.Value)

            let monthDayDates =
                // 2) Month/day without year: 01-15, 1/5, etc (screenshots)
                // Avoid matching the first two segments of full dates like 11/01/26 (the (?![/-]\d) part).
                // Also avoid matching the last two segments of full dates like 20/01/26 -> 01/26 (the (?<!\d[/-]) part).
                Regex.Matches(text, @"(?<!\d[/-])\b\d{1,2}[/-]\d{1,2}\b(?![/-]\d)")
                |> Seq.cast<Match>
                |> Seq.choose (fun m ->
                    // Skip wallet progress counters like "1/8 stamps", "0/8 Stamps" etc.
                    let v = m.Value
                    if v.Contains("/") then
                        let startIdx = max 0 (m.Index - 10)
                        let endIdx = min text.Length (m.Index + m.Length + 15)
                        let around = text.Substring(startIdx, endIdx - startIdx)
                        if Regex.IsMatch(around, @"(?i)\bstamps?\b") then
                            None
                        else
                            tryParseDateAny nowUtc v
                    else
                        tryParseDateAny nowUtc v)

            let monthNameDates =
                // 3) Month names: "15 Jan" / "Jan 15"
                Regex.Matches(text, @"(?i)\b(?:\d{1,2}\s*(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t)?(?:ember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)|(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t)?(?:ember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\s*\d{1,2})\b")
                |> Seq.cast<Match>
                |> Seq.choose (fun m -> tryParseDateAny nowUtc m.Value)

            Seq.concat [ numericDates; monthDayDates; monthNameDates ]
            |> Seq.map (fun dt -> dt.Date)
            |> Seq.distinct
            |> Seq.sort
            |> Seq.toArray

type CouponOcrEngine(azureTextOcr: IBotOcr, logger: ILogger<CouponOcrEngine>, time: TimeProvider) =
    let noneMoney: Nullable<decimal> = Nullable()
    let noneDate: Nullable<DateTime> = Nullable()
    let noneBarcode: string | null = null

    let someMoney (v: decimal) : Nullable<decimal> = Nullable(v)
    let someDate (v: DateTime) : Nullable<DateTime> = Nullable(v)

    let tryDecodeNative (imageBytes: ReadOnlyMemory<byte>) =
        use activity = botActivity.StartActivity("couponOcr.barcode.native")
        try
            let result = CouponBarcodeReader.tryDecode imageBytes
            if not (isNull activity) then
                %activity.SetTag("barcode.found", result.IsSome)
            result
        with ex ->
            if not (isNull activity) then
                %activity.SetTag("error.type", ex.GetType().FullName)
                %activity.SetStatus(ActivityStatusCode.Error)
            logger.LogWarning(ex, "Native barcode decode failed; trying managed decoder")
            None

    let tryDecodeManaged (imageBytes: ReadOnlyMemory<byte>) =
        use activity = botActivity.StartActivity("couponOcr.barcode.managed")
        let mutable attempts = 0
        try
            use original =
                use load = botActivity.StartActivity("couponOcr.image.load")
                use ms = new MemoryStream(imageBytes.ToArray())
                Image.Load<Rgba32>(ms)
            if not (isNull activity) then
                %activity.SetTag("image.width", original.Width)
                %activity.SetTag("image.height", original.Height)
            let createReader () =
                let opts = DecodingOptions()
                opts.TryHarder <- true
                opts.PossibleFormats <- [| BarcodeFormat.EAN_13 |]
                opts.TryInverted <- false
                let reader = BarcodeReaderGeneric(null, (fun source -> BarcodeBinarizer(source) :> Binarizer), null)
                reader.Options <- opts
                // The EAN-13 reader retains its own rotation fallback through RotateSupported.
                reader.AutoRotate <- false
                reader

            let reader = createReader ()
            let parallelOptions = ParallelOptions(MaxDegreeOfParallelism = Environment.ProcessorCount)

            let decode (decoder: BarcodeReaderGeneric) (label: string) (img: LuminanceSource) =
                use attempt = botActivity.StartActivity("couponOcr.barcode.decode")
                %Interlocked.Increment(&attempts)
                if not (isNull attempt) then
                    %attempt.SetTag("barcode.strategy", label)
                    %attempt.SetTag("image.width", img.Width)
                    %attempt.SetTag("image.height", img.Height)
                let res = decoder.Decode(img)
                if not (isNull attempt) then
                    %attempt.SetTag("barcode.found", not (isNull res) && not (String.IsNullOrWhiteSpace res.Text))
                if isNull res || String.IsNullOrWhiteSpace res.Text then
                    logger.LogDebug("Barcode not found by ZXing ({label})", label)
                    None
                else
                    logger.LogDebug("Barcode decoded by ZXing ({label}): {text}", label, res.Text)
                    Some res.Text

            let clamp (v: int) (minv: int) (maxv: int) = max minv (min maxv v)

            let cropTry (decoder: BarcodeReaderGeneric) (baseLabel: string) (img: LuminanceSource) (rect: Rectangle) =
                let x = clamp rect.X 0 (img.Width - 1)
                let y = clamp rect.Y 0 (img.Height - 1)
                let w = clamp rect.Width 1 (img.Width - x)
                let h = clamp rect.Height 1 (img.Height - y)
                let cropped = img.crop(x, y, w, h)
                decode decoder baseLabel cropped

            let tryOnImage (labelPrefix: string) (img: Image<Rgba32>) =
                // Strategy:
                // - Full image (fast path)
                // - Top crops (barcodes are often in the upper half on receipt photos)
                // - Middle band crops (covers centre region)
                // - Bottom crops (useful for app screenshots)
                // - Center-narrowed variants of the above (avoid noise on sides)
                let luminance = BarcodeLuminanceSource(ImageSharpLuminanceSource<Rgba32>(img))
                match decode reader (labelPrefix + ":full") luminance with
                | Some t -> Some t
                | None ->
                    let w = img.Width
                    let h = img.Height

                    // Top crops: from y=0, covering the upper portion
                    let topCrops =
                        [| 0.50; 0.60 |]
                        |> Array.map (fun endFrac ->
                            let hh = int (Math.Round(float h * endFrac))
                            Rectangle(0, 0, w, hh))

                    // Middle band crops: horizontal slices through the centre
                    let middleBandCrops =
                        [| (0.20, 0.70); (0.30, 0.80) |]
                        |> Array.map (fun (startFrac, endFrac) ->
                            let y = int (Math.Round(float h * startFrac))
                            let hh = int (Math.Round(float h * endFrac)) - y
                            Rectangle(0, y, w, hh))

                    // Bottom crops: from a start fraction down to the bottom
                    let bottomCrops =
                        [| 0.45; 0.55; 0.65 |]
                        |> Array.map (fun startFrac ->
                            let y = int (Math.Round(float h * startFrac))
                            Rectangle(0, y, w, h - y))

                    let fullWidthCrops =
                        Array.concat [| topCrops; middleBandCrops; bottomCrops |]

                    // Center-narrowed variants (80% and 60% width) for each full-width crop
                    let centerCrops =
                        fullWidthCrops
                        |> Array.collect (fun r ->
                            [| // 80% width
                               let x1 = int (Math.Round(float w * 0.10))
                               let ww1 = w - 2 * x1
                               Rectangle(x1, r.Y, ww1, r.Height)
                               // 60% width
                               let x2 = int (Math.Round(float w * 0.20))
                               let ww2 = w - 2 * x2
                               Rectangle(x2, r.Y, ww2, r.Height) |])

                    let allRects =
                        Array.append fullWidthCrops centerCrops

                    match parallelOptions.MaxDegreeOfParallelism with
                    | 1 ->
                        allRects
                        |> Array.mapi (fun i r -> (i, r))
                        |> Array.tryPick (fun (i, r) -> cropTry reader $"{labelPrefix}:crop{i}" luminance r)
                    | _ ->
                        let results = Array.create allRects.Length None
                        %Parallel.For<BarcodeReaderGeneric>(
                            0, allRects.Length, parallelOptions,
                            Func<BarcodeReaderGeneric>(createReader),
                            Func<int, ParallelLoopState, BarcodeReaderGeneric, BarcodeReaderGeneric>(fun i state decoder ->
                                let result = cropTry decoder $"{labelPrefix}:crop{i}" luminance allRects[i]
                                results[i] <- result
                                if result.IsSome then
                                    state.Break()
                                decoder),
                            Action<BarcodeReaderGeneric>(fun _ -> ()))
                        // Break completes lower indices so crop priority is independent of scheduling.
                        results |> Array.tryPick id

            let tryAll () =
                match tryOnImage "orig" original with
                | Some t -> Some t
                | None ->
                    // Preprocess: grayscale + contrast helps with noisy receipt photos
                    // where ghost text bleeds through from the back of the paper.
                    use preprocessed =
                        use preprocess = botActivity.StartActivity("couponOcr.image.preprocess")
                        original.Clone(fun ctx ->
                            ctx.Grayscale() |> ignore
                            ctx.Contrast(1.5f) |> ignore)

                    match tryOnImage "gray" preprocessed with
                    | Some t -> Some t
                    | None ->
                        // Resize to a different width and retry. ZXing often behaves better
                        // when the barcode region occupies more of the image (downscale),
                        // or when barcode bars are wider (upscale narrow photos).
                        let tryResize (label: string) (source: Image<Rgba32>) (targetWidth: int) =
                            if source.Width = targetWidth then
                                None
                            else
                                let scale = float targetWidth / float source.Width
                                let targetHeight = max 1 (int (Math.Round(float source.Height * scale)))
                                use resized =
                                    use resize = botActivity.StartActivity("couponOcr.image.resize")
                                    if not (isNull resize) then
                                        %resize.SetTag("image.width", targetWidth)
                                        %resize.SetTag("image.height", targetHeight)
                                    source.Clone(fun ctx -> ctx.Resize(targetWidth, targetHeight) |> ignore)
                                tryOnImage $"{label}{targetWidth}" resized

                        let resizeTargets = [| 1000; 800; 1400 |]

                        resizeTargets
                        |> Array.tryPick (fun tw ->
                            match tryResize "w" original tw with
                            | Some t -> Some t
                            | None -> tryResize "gw" preprocessed tw)

            let result = tryAll ()
            if not (isNull activity) then
                %activity.SetTag("barcode.attempts", attempts)
                %activity.SetTag("barcode.found", result.IsSome)
            match result with
            | Some text -> text
            | None -> noneBarcode
        with ex ->
            if not (isNull activity) then
                %activity.SetTag("barcode.attempts", attempts)
                %activity.SetTag("error.type", ex.GetType().FullName)
                %activity.SetStatus(ActivityStatusCode.Error)
            logger.LogWarning(ex, "Barcode decode failed")
            noneBarcode

    let tryDecodeBarcode (imageBytes: ReadOnlyMemory<byte>) =
        use activity = botActivity.StartActivity("couponOcr.barcode")
        let result =
            match tryDecodeNative imageBytes with
            | Some text -> text
            | None -> tryDecodeManaged imageBytes
        if not (isNull activity) then
            %activity.SetTag("barcode.found", not (String.IsNullOrWhiteSpace result))
        result

    let parseFromText (nowUtc: DateTime) (ocrText: string) =
        let amounts = CouponOcrParsing.findEuroAmounts ocrText
        let dates = CouponOcrParsing.findDates nowUtc ocrText

        let couponValue, minCheck =
            match CouponOcrParsing.tryParseDiscountAndThreshold ocrText with
            | Some (v, mc) ->
                someMoney v, someMoney mc
            | None ->
                if amounts.Length >= 2 then
                    let v = amounts |> Array.min
                    let mc = amounts |> Array.max
                    someMoney v, someMoney mc
                elif amounts.Length = 1 then
                    someMoney amounts[0], noneMoney
                else
                    noneMoney, noneMoney

        let validFrom, validTo =
            match CouponOcrParsing.tryParseValidRange nowUtc ocrText with
            | Some (a, b) ->
                someDate a, someDate b
            | None ->
                if dates.Length >= 2 then
                    someDate dates[0], someDate dates[dates.Length - 1]
                elif dates.Length = 1 then
                    noneDate, someDate dates[0]
                else
                    noneDate, noneDate

        couponValue, minCheck, validFrom, validTo

    member _.Recognize(imageBytes: ReadOnlyMemory<byte>) =
        task {
            use activity = botActivity.StartActivity("couponOcr.recognize")
            if not (isNull activity) then
                %activity.SetTag("image.size_bytes", imageBytes.Length)
            let nowUtc = time.GetUtcNow().UtcDateTime
            let barcode = tryDecodeBarcode imageBytes

            // The Azure OCR SDK already retried transient failures; if one still surfaces (network,
            // timeout, or a non-2xx response after retries) AnalyzeImageBytes re-throws it. Catch it
            // here (don't crash) and record that the OCR *backend* failed, so callers can tell "Azure
            // was down/timed out/errored" apart from "Azure answered 200 but found no usable text".
            // The ZXing barcode above is independent of Azure and may still be set.
            let! ocrAnalysis, backendFailed =
                task {
                    use azure = botActivity.StartActivity("couponOcr.azure")
                    try
                        let! a = azureTextOcr.AnalyzeImageBytes(imageBytes)
                        return a, false
                    with ex ->
                        if not (isNull azure) then
                            %azure.SetTag("error.type", ex.GetType().FullName)
                            %azure.SetStatus(ActivityStatusCode.Error)
                        logger.LogWarning(ex, "Azure OCR backend call failed after retries")
                        return (null: OcrAnalysis | null), true
                }

            let ocrText = if isNull ocrAnalysis then null else ocrAnalysis.Text
            use parse = botActivity.StartActivity("couponOcr.parse")

            let couponValue, minCheck, validFrom, validTo =
                if String.IsNullOrWhiteSpace ocrText then
                    noneMoney, noneMoney, noneDate, noneDate
                else
                    parseFromText nowUtc ocrText

            let barcode =
                if not (String.IsNullOrWhiteSpace barcode) then
                    barcode
                elif not (String.IsNullOrWhiteSpace ocrText) then
                    let m = Regex.Match(ocrText, @"\b(\d{13})\b")
                    if m.Success then
                        logger.LogDebug("Barcode found via OCR text fallback: {text}", m.Groups.[1].Value)
                        m.Groups.[1].Value
                    else
                        noneBarcode
                else
                    noneBarcode

            return
                { couponValue = couponValue
                  minCheck = minCheck
                  validFrom = validFrom
                  validTo = validTo
                  barcode = barcode
                  backendFailed = backendFailed }
        }
