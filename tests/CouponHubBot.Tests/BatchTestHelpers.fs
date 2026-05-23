namespace CouponHubBot.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading.Tasks
open BotTestInfra
open FakeCallHelpers

/// Shared helpers for album-batch test files. Kept here so multiple test classes
/// (BatchAddFlowTests, BatchStateMachineTests, BatchConcurrencyTests, …) can use
/// the same poll / call-filter primitives without copy-paste.
///
/// All polling helpers fail loudly on timeout — a flake surfaces as a clear
/// "timed out waiting for X" rather than letting stale state pass an assertion.
module BatchTestHelpers =

    let pollIntervalMs = 25

    let solutionDirPath = DotNet.Testcontainers.Builders.CommonDirectoryPath.GetSolutionDirectory().DirectoryPath

    let readImageBytes (fileName: string) =
        File.ReadAllBytes(Path.Combine(solutionDirPath, "tests", "CouponHubBot.Ocr.Tests", "Images", fileName))

    let readAzureCacheJson (fileName: string) =
        File.ReadAllText(Path.Combine(solutionDirPath, "tests", "CouponHubBot.Ocr.Tests", "AzureCache", fileName + ".azure.json"))

    /// Azure JSON with any 13-digit barcode text line removed (forces
    /// CouponOcrEngine's text-fallback to NOT find a barcode either).
    let stripBarcodeFromAzureJson (azureJson: string) =
        let doc = JsonDocument.Parse(azureJson)
        use ms = new MemoryStream()
        let opts = JsonWriterOptions(Indented = false)
        use writer = new Utf8JsonWriter(ms, opts)
        let rec writeElement (el: JsonElement) =
            match el.ValueKind with
            | JsonValueKind.Object ->
                writer.WriteStartObject()
                for prop in el.EnumerateObject() do
                    writer.WritePropertyName(prop.Name)
                    writeElement prop.Value
                writer.WriteEndObject()
            | JsonValueKind.Array ->
                writer.WriteStartArray()
                let mutable items = el.EnumerateArray() |> Seq.toArray
                if items.Length > 0
                   && items |> Array.exists (fun it ->
                       it.ValueKind = JsonValueKind.Object
                       && (match it.TryGetProperty("text") with
                           | true, t -> System.Text.RegularExpressions.Regex.IsMatch(t.GetString(), @"^\d{13}$")
                           | _ -> false)) then
                    items <- items |> Array.filter (fun it ->
                        not (it.ValueKind = JsonValueKind.Object
                             && (match it.TryGetProperty("text") with
                                 | true, t -> System.Text.RegularExpressions.Regex.IsMatch(t.GetString(), @"^\d{13}$")
                                 | _ -> false)))
                for item in items do
                    writeElement item
                writer.WriteEndArray()
            | _ -> el.WriteTo(writer)
        writeElement doc.RootElement
        writer.Flush()
        System.Text.Encoding.UTF8.GetString(ms.ToArray())

    // ── Call-filter helpers ─────────────────────────────────────────────

    /// Calls whose text contains the album-placeholder marker for a given chat.
    let placeholderCalls (calls: FakeCall array) (chatId: int64) =
        calls
        |> Array.filter (fun call ->
            match parseCallBody call.Body with
            | Some p when p.ChatId = Some chatId ->
                match p.Text with
                | Some t -> t.Contains("обрабатываю купоны")
                | _ -> false
            | _ -> false)

    /// Calls whose text looks like the album bulk-confirm message
    /// (either "Подтвердить N купонов:" or the all-failed "Не смог…").
    let bulkConfirmCalls (calls: FakeCall array) (chatId: int64) =
        calls
        |> Array.filter (fun call ->
            match parseCallBody call.Body with
            | Some p when p.ChatId = Some chatId ->
                match p.Text with
                | Some t -> t.Contains("Подтвердить") || t.Contains("Не смог распознать ни одного")
                | _ -> false
            | _ -> false)

    /// Extracts `reply_parameters.message_id` from a sendMessage body, if present.
    let getReplyToMessageId (body: string) : int option =
        try
            use doc = JsonDocument.Parse(body)
            match doc.RootElement.TryGetProperty("reply_parameters") with
            | true, rp ->
                match rp.TryGetProperty("message_id") with
                | true, mid when mid.ValueKind = JsonValueKind.Number -> Some(mid.GetInt32())
                | _ -> None
            | _ -> None
        with _ -> None

    /// Calls that are explicit replies (reply_parameters present) to a given chat,
    /// optionally filtered by text substring.
    let replyCalls (calls: FakeCall array) (chatId: int64) (textContains: string option) =
        calls
        |> Array.filter (fun call ->
            match parseCallBody call.Body with
            | Some p when p.ChatId = Some chatId ->
                let hasReply = (getReplyToMessageId call.Body).IsSome
                let textOk =
                    match textContains, p.Text with
                    | None, _ -> true
                    | Some sub, Some t -> t.Contains(sub)
                    | Some _, None -> false
                hasReply && textOk
            | _ -> false)

    // ── Fake-OCR setup helpers ──────────────────────────────────────────

    let resetOcrFakes (fixture: OcrCouponHubTestContainers) =
        task {
            do! fixture.SetAzureOcrDelay(0)
            do! fixture.SetAzureOcrErrorMode("")
            do! fixture.SetAzureOcrScript([||])
        }

    let setupGoodOcr (fixture: OcrCouponHubTestContainers) (fileId: string) (fileName: string) =
        task {
            do! fixture.SetTelegramFile(fileId, readImageBytes fileName)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson fileName)
        }

    /// Past the seeded BATCH_DEBOUNCE_MS (30000) so the debounce timer fires
    /// when Advance is called.
    let advancePastDebounce (fixture: OcrCouponHubTestContainers) =
        fixture.AdvanceBotClock(31_000)

    // ── Polling helpers ─────────────────────────────────────────────────

    let waitForBatchByUser (fixture: OcrCouponHubTestContainers) (userId: int64) (timeoutMs: int) =
        task {
            let sw = Stopwatch.StartNew()
            let mutable batchId = 0L
            while sw.ElapsedMilliseconds < int64 timeoutMs && batchId = 0L do
                let! id =
                    fixture.QuerySingle<int64>(
                        "SELECT COALESCE((SELECT id FROM pending_add_batch WHERE user_id=@u ORDER BY id DESC LIMIT 1), 0)",
                        {| u = userId |})
                batchId <- id
                if batchId = 0L then do! Task.Delay pollIntervalMs
            if batchId = 0L then
                return failwith $"Timeout: no batch row for user {userId} after {timeoutMs}ms"
            else
                return batchId
        }

    let waitForAllItemsTerminal (fixture: OcrCouponHubTestContainers) (batchId: int64) (timeoutMs: int) =
        task {
            let sw = Stopwatch.StartNew()
            let mutable pendingCount = -1L
            while sw.ElapsedMilliseconds < int64 timeoutMs && pendingCount <> 0L do
                let! c =
                    fixture.QuerySingle<int64>(
                        "SELECT COUNT(*)::bigint FROM pending_add_batch_item WHERE batch_id=@b AND status='pending'",
                        {| b = batchId |})
                pendingCount <- c
                if pendingCount <> 0L then do! Task.Delay pollIntervalMs
            if pendingCount <> 0L then
                failwith $"Timeout: {pendingCount} item(s) still pending in batch {batchId} after {timeoutMs}ms"
        }

    let waitForBatchStatus (fixture: OcrCouponHubTestContainers) (batchId: int64) (expected: string) (timeoutMs: int) =
        task {
            let sw = Stopwatch.StartNew()
            let mutable status = ""
            while sw.ElapsedMilliseconds < int64 timeoutMs && status <> expected do
                let! s =
                    fixture.QuerySingle<string>(
                        "SELECT COALESCE((SELECT status FROM pending_add_batch WHERE id=@b LIMIT 1), '__GONE__')",
                        {| b = batchId |})
                status <- s
                if status <> expected then do! Task.Delay pollIntervalMs
            if status <> expected then
                failwith $"Timeout: batch {batchId} status is '{status}', expected '{expected}' after {timeoutMs}ms"
        }

    let waitForBatchCleared (fixture: OcrCouponHubTestContainers) (batchId: int64) (timeoutMs: int) =
        task {
            let sw = Stopwatch.StartNew()
            let mutable exists = true
            while sw.ElapsedMilliseconds < int64 timeoutMs && exists do
                let! c =
                    fixture.QuerySingle<int64>(
                        "SELECT COUNT(*)::bigint FROM pending_add_batch WHERE id=@b",
                        {| b = batchId |})
                exists <- c > 0L
                if exists then do! Task.Delay pollIntervalMs
            if exists then
                failwith $"Timeout: batch {batchId} not cleared after {timeoutMs}ms"
        }

    let waitForAzureCallCount (fixture: OcrCouponHubTestContainers) (expected: int) (timeoutMs: int) =
        task {
            let sw = Stopwatch.StartNew()
            let mutable count = 0
            while sw.ElapsedMilliseconds < int64 timeoutMs && count < expected do
                let! calls = fixture.GetAzureOcrCalls()
                count <- calls.Length
                if count < expected then do! Task.Delay pollIntervalMs
            if count < expected then
                failwith $"Timeout: Azure OCR call count is {count}, expected ≥{expected} after {timeoutMs}ms"
        }

    /// Wait for the bulk-confirm sendMessage to actually land in the fake-tg call
    /// log for `userId`'s chat. waitForBatchStatus "awaiting_user" returns the
    /// moment FinalizeBatch flips the row's status — but the SendMessage that
    /// produces the bulk-confirm call happens a few async steps later. Polling
    /// status returns too early, so we poll the fake-tg log directly here.
    let waitForBulkConfirmCall (fixture: OcrCouponHubTestContainers) (userId: int64) (timeoutMs: int) =
        task {
            let sw = Stopwatch.StartNew()
            let mutable found = false
            while sw.ElapsedMilliseconds < int64 timeoutMs && not found do
                let! calls = fixture.GetFakeCalls("sendMessage")
                let bulks = bulkConfirmCalls calls userId
                found <- bulks.Length > 0
                if not found then do! Task.Delay pollIntervalMs
            if not found then
                failwith $"Timeout: no bulk-confirm sendMessage for user {userId} after {timeoutMs}ms"
        }

    /// Wait until `fixture.GetFakeCalls("sendMessage")` contains at least one call
    /// to `chatId` whose text satisfies `textPredicate`. Used to assert specific
    /// reply texts (per-photo replies, abandonment messages, etc.) without
    /// guessing about timing.
    let waitForSendMessageMatching
        (fixture: OcrCouponHubTestContainers)
        (chatId: int64)
        (textPredicate: string -> bool)
        (timeoutMs: int) =
        task {
            let sw = Stopwatch.StartNew()
            let mutable found = false
            while sw.ElapsedMilliseconds < int64 timeoutMs && not found do
                let! calls = fixture.GetFakeCalls("sendMessage")
                found <-
                    calls
                    |> Array.exists (fun call ->
                        match parseCallBody call.Body with
                        | Some p when p.ChatId = Some chatId ->
                            match p.Text with
                            | Some t -> textPredicate t
                            | _ -> false
                        | _ -> false)
                if not found then do! Task.Delay pollIntervalMs
            if not found then
                failwith $"Timeout: no sendMessage matching predicate to chat {chatId} after {timeoutMs}ms"
        }

    /// Wait until the `pending_add_batch.bulk_message_id` for a batch differs
    /// from `oldMessageId`. Used to confirm a finalize completed its full chain
    /// (DeleteMessage + SendMessage + SetBatchBulkMessageId).
    let waitForBulkMessageIdChange
        (fixture: OcrCouponHubTestContainers)
        (batchId: int64)
        (oldMessageId: int)
        (timeoutMs: int) =
        task {
            let sw = Stopwatch.StartNew()
            let mutable changed = false
            while sw.ElapsedMilliseconds < int64 timeoutMs && not changed do
                let! current =
                    fixture.QuerySingle<int>(
                        "SELECT COALESCE((SELECT bulk_message_id FROM pending_add_batch WHERE id=@b LIMIT 1), 0)",
                        {| b = batchId |})
                changed <- current <> oldMessageId && current <> 0
                if not changed then do! Task.Delay pollIntervalMs
            if not changed then
                failwith $"Timeout: bulk_message_id for batch {batchId} did not change from {oldMessageId} after {timeoutMs}ms"
        }

    /// Polls both sendMessage AND editMessageText call logs — many flows
    /// (cancel summary, confirm summary, supersede message) use EditMessageText
    /// on the existing bulk message instead of sending a new one.
    let waitForSendMessageOrEditMatching
        (fixture: OcrCouponHubTestContainers)
        (chatId: int64)
        (textPredicate: string -> bool)
        (timeoutMs: int) =
        task {
            let sw = Stopwatch.StartNew()
            let mutable found = false
            while sw.ElapsedMilliseconds < int64 timeoutMs && not found do
                let! sends = fixture.GetFakeCalls("sendMessage")
                let! edits = fixture.GetFakeCalls("editMessageText")
                let scan (calls: FakeCall array) =
                    calls
                    |> Array.exists (fun call ->
                        match parseCallBody call.Body with
                        | Some p when p.ChatId = Some chatId ->
                            match p.Text with
                            | Some t -> textPredicate t
                            | _ -> false
                        | _ -> false)
                found <- scan sends || scan edits
                if not found then do! Task.Delay pollIntervalMs
            if not found then
                failwith $"Timeout: no sendMessage/editMessageText matching predicate to chat {chatId} after {timeoutMs}ms"
        }

    /// Waits for a `pending_add` row to reach a specific stage.
    let waitForPendingAddStage
        (fixture: OcrCouponHubTestContainers)
        (userId: int64)
        (expected: string)
        (timeoutMs: int) =
        task {
            let sw = Stopwatch.StartNew()
            let mutable stage = ""
            while sw.ElapsedMilliseconds < int64 timeoutMs && stage <> expected do
                let! s =
                    fixture.QuerySingle<string>(
                        "SELECT COALESCE((SELECT stage FROM pending_add WHERE user_id=@u LIMIT 1), '__GONE__')",
                        {| u = userId |})
                stage <- s
                if stage <> expected then do! Task.Delay pollIntervalMs
            if stage <> expected then
                failwith $"Timeout: pending_add for user {userId} stage is '{stage}', expected '{expected}' after {timeoutMs}ms"
        }

    /// Waits for the `pending_add` row for a user to be gone (no row).
    let waitForPendingAddRowGone
        (fixture: OcrCouponHubTestContainers)
        (userId: int64)
        (timeoutMs: int) =
        task {
            let sw = Stopwatch.StartNew()
            let mutable exists = true
            while sw.ElapsedMilliseconds < int64 timeoutMs && exists do
                let! c =
                    fixture.QuerySingle<int64>(
                        "SELECT COUNT(*)::bigint FROM pending_add WHERE user_id=@u",
                        {| u = userId |})
                exists <- c > 0L
                if exists then do! Task.Delay pollIntervalMs
            if exists then
                failwith $"Timeout: pending_add for user {userId} not cleared after {timeoutMs}ms"
        }

    /// Waits for a batch's item count to reach the given value.
    let waitForItemCount
        (fixture: OcrCouponHubTestContainers)
        (batchId: int64)
        (expected: int)
        (timeoutMs: int) =
        task {
            let sw = Stopwatch.StartNew()
            let mutable count = 0L
            while sw.ElapsedMilliseconds < int64 timeoutMs && int count <> expected do
                let! c =
                    fixture.QuerySingle<int64>(
                        "SELECT COUNT(*)::bigint FROM pending_add_batch_item WHERE batch_id=@b",
                        {| b = batchId |})
                count <- c
                if int count <> expected then do! Task.Delay pollIntervalMs
            if int count <> expected then
                failwith $"Timeout: batch {batchId} has {count} items, expected {expected} after {timeoutMs}ms"
        }
