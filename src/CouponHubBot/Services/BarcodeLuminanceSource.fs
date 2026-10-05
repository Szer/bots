namespace CouponHubBot.Services

open System
open ZXing

type internal BarcodeLuminanceSource private
    (pixels: byte[], width: int, height: int, offset: int, xStride: int, yStride: int) =
    inherit LuminanceSource(width, height)

    new(source: LuminanceSource) =
        BarcodeLuminanceSource(source.Matrix, source.Width, source.Height, 0, 1, source.Width)

    override _.getRow(y, row) =
        if y < 0 || y >= height then
            invalidArg (nameof y) "Row must be within the image."
        let result = if isNull row || row.Length < width then Array.zeroCreate width else row
        let start = offset + y * yStride
        match xStride with
        | 1 -> pixels.AsSpan(start, width).CopyTo(result.AsSpan())
        | _ ->
            for x = 0 to width - 1 do
                result[x] <- pixels[start + x * xStride]
        result

    override this.Matrix =
        let result = Array.zeroCreate<byte> (width * height)
        let row = Array.zeroCreate<byte> width
        for y = 0 to height - 1 do
            this.getRow(y, row).AsSpan(0, width).CopyTo(result.AsSpan(y * width, width))
        result

    override _.CropSupported = true
    override _.RotateSupported = true
    override _.InversionSupported = true
    override this.invert() = InvertedLuminanceSource(this)

    override _.crop(left, top, cropWidth, cropHeight) =
        if left < 0 || top < 0 || cropWidth <= 0 || cropHeight <= 0
           || left > width - cropWidth || top > height - cropHeight then
            invalidArg (nameof cropWidth) "Crop must fit within the image."
        BarcodeLuminanceSource(pixels, cropWidth, cropHeight, offset + left * xStride + top * yStride, xStride, yStride)

    override _.rotateCounterClockwise() =
        BarcodeLuminanceSource(pixels, height, width, offset + (width - 1) * xStride, yStride, -xStride)
