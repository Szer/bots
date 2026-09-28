namespace CouponHubBot.Ocr.Tests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open Microsoft.Extensions.Logging.Abstractions
open SixLabors.ImageSharp
open SixLabors.ImageSharp.PixelFormats
open Xunit
open BotInfra
open CouponHubBot.Services

type TracingTests() =
    [<Theory>]
    [<InlineData(false, false)>]
    [<InlineData(true, false)>]
    [<InlineData(false, true)>]
    member _.``Recognition traces local work and backend failures under the caller``(invalidImage: bool, backendFails: bool) =
        task {
            let spans = ConcurrentQueue<Activity>()
            use parent = new Activity("ocr-test")
            %parent.Start()
            use listener =
                new ActivityListener(
                    ShouldListenTo = (fun source -> source.Name = "CouponHubBot"),
                    Sample = (fun _ -> ActivitySamplingResult.AllDataAndRecorded),
                    ActivityStopped = (fun span ->
                        if span.TraceId = parent.TraceId then spans.Enqueue span))
            ActivitySource.AddActivityListener listener

            let bytes =
                if invalidImage then [| 0uy |]
                else
                    use image = new Image<Rgba32>(64, 4)
                    use stream = new MemoryStream()
                    image.SaveAsPng(stream)
                    stream.ToArray()

            let backend =
                { new IBotOcr with
                    member _.AnalyzeImageBytes(_) =
                        task {
                            Assert.Equal("couponOcr.azure", Activity.Current.OperationName)
                            do! Task.Delay 20
                            if backendFails then return raise (TimeoutException())
                            else return { RawJson = "{}"; Text = "€10 OFF €50" }
                        } }
            let engine = CouponOcrEngine(backend, NullLogger<CouponOcrEngine>.Instance, TimeProvider.System)
            let! result = engine.Recognize(ReadOnlyMemory<byte>(bytes))
            Assert.Equal(backendFails, result.backendFailed)
            if not backendFails then Assert.Equal(10M, result.couponValue.Value)

            let single name = spans |> Seq.filter (fun span -> span.OperationName = name) |> Assert.Single
            let recognize = single "couponOcr.recognize"
            let barcode = single "couponOcr.barcode"
            let load = single "couponOcr.image.load"
            let azure = single "couponOcr.azure"
            let parse = single "couponOcr.parse"
            Assert.Equal(parent.SpanId, recognize.ParentSpanId)
            for span in [ barcode; azure; parse ] do
                Assert.Equal(recognize.SpanId, span.ParentSpanId)
            Assert.Equal(barcode.SpanId, load.ParentSpanId)
            Assert.True(barcode.StartTimeUtc + barcode.Duration <= azure.StartTimeUtc)
            Assert.True(azure.StartTimeUtc + azure.Duration <= parse.StartTimeUtc)
            Assert.True(azure.Duration >= TimeSpan.FromMilliseconds 20.)
            Assert.Equal(box bytes.Length, recognize.GetTagItem("image.size_bytes"))
            Assert.Equal((if backendFails then ActivityStatusCode.Error else ActivityStatusCode.Unset), azure.Status)

            let attempts = spans |> Seq.filter (fun span -> span.OperationName = "couponOcr.barcode.decode") |> Seq.toArray
            Assert.Equal(box attempts.Length, barcode.GetTagItem("barcode.attempts"))
            if invalidImage then
                Assert.Empty attempts
                Assert.Equal(ActivityStatusCode.Error, barcode.Status)
            else
                Assert.True(attempts.Length > 1)
                Assert.Equal(box false, barcode.GetTagItem("barcode.found"))
                Assert.Equal(box "orig:full", attempts[0].GetTagItem("barcode.strategy"))
                for attempt in attempts do
                    Assert.Equal(barcode.SpanId, attempt.ParentSpanId)
                    Assert.Equal(box false, attempt.GetTagItem("barcode.found"))
                %single "couponOcr.image.preprocess"
                Assert.Contains(spans, fun span -> span.OperationName = "couponOcr.image.resize")
        }
