# OCR Pipeline

## Overview

`CouponOcrEngine` combines barcode scanning (ZXing) with Azure Computer Vision text OCR to extract coupon data from photos.

## Components

- **ZXingCpp** + ImageSharp — EAN-13 scanning on grayscale images, followed by horizontal crops
- **ZXing.Net** + ImageSharp bindings — fallback with rotation, contrast and resizing; luminance is shared across crops
- **AzureOcrService** — calls Azure Computer Vision `imageanalysis:analyze` endpoint
- **CouponOcrEngine** — orchestrates both, returns `CouponOCR` record

Native library load or decode failures fall back to the managed decoder. The Linux native binaries require glibc 2.38 and libstdc++ with GLIBCXX_3.4.31; the Noble runtime image provides both.

## OCR Result

`CouponOCR` contains optional fields: `value`, `min_check`, `expires_at`, `barcode_text`. Any combination may be present. The result is **always a pre-fill suggestion** — the user confirms final values in the wizard.

## Caching (for tests)

`tests/CouponHubBot.Ocr.Tests/AzureCache/` stores Azure OCR responses as `*.azure.json` files. These are git-tracked so tests can run without Azure API keys (cache-only mode).

## Configuration

Env vars:
- `OCR_ENABLED` — enable/disable OCR (default: false)
- `OCR_MAX_FILE_SIZE_BYTES` — max photo size for OCR processing
- `AZURE_OCR_ENDPOINT` — Azure Computer Vision endpoint URL
- `AZURE_OCR_KEY` — Azure API key
