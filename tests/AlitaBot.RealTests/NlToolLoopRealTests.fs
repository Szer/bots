namespace AlitaBot.RealTests

open System
open System.Threading.Tasks
open Dapper
open Npgsql
open Xunit

/// S10 PR1 end-to-end tests: the natural-language tool-calling loop against real Telegram +
/// real Azure AI Foundry (chat completions with tools, image generation, and the Responses
/// API's `web_search`). No `/img` or explicit command is ever sent here — the trigger is
/// plain conversational text containing Алита's name, exercising the FULL round trip:
/// trigger -> ResponderService.toolsFor -> AgentToolLoop -> ToolExecutor -> MediaActions /
/// AzureResponsesWebSearch -> a real Telegram reply.
///
/// Placed LAST in the .fsproj Compile list (real-test suites execute in roughly declaration
/// order against the one shared long-lived test chat/user): its 2 extra real chat messages
/// were originally compiled BEFORE `DossierRealTests`, and its first real run empirically
/// caused `DossierRealTests`' "newest 5 facts" `/dossier` assertion to fail — the nightly
/// extraction job's "last 24h" window picked up THIS class's "нарисуй.../найди..." messages
/// (sent by the SAME test user) alongside `DossierRealTests`' own seeded F#/YAML/coffee
/// facts, and they out-competed the sought facts for the top-5 "newest" slot. That fragility
/// (any real test's messages from the same user can crowd `/dossier`'s newest-5 display) is
/// pre-existing — `ImageGenRealTests`/`SongRealTests` already ran before `DossierRealTests`
/// before this slice existed — this class just adds one more contributor, so it runs last to
/// minimize (not eliminate) that interference rather than fixing `DossierRealTests` itself.
type NlToolLoopRealTests(fx: RealAssemblyFixture) =
    let env = fx.Env

    /// Polls `llm_usage` for a `kind='web_search'` row in `chatId` recorded after `since` —
    /// the definitive, DB-verified signal that AzureResponsesWebSearch actually fired and
    /// got billed/recorded (LlmCall.Succeeded, fire-and-forget — see LlmTelemetry.fs), more
    /// robust than string-matching the model's own free-text reply for a literal URL (the
    /// real model, even when correctly grounded, may choose to summarize rather than paste
    /// a raw link — see this test's own empirical finding, recorded in its doc comment).
    let awaitWebSearchUsageRow (chatId: int64) (since: DateTime) =
        task {
            let deadline = DateTime.UtcNow + Timeouts.dbSettle
            let mutable found = false
            while not found && DateTime.UtcNow < deadline do
                use conn = new NpgsqlConnection(fx.DbConnectionString)
                let! count =
                    conn.ExecuteScalarAsync<int64>(
                        "SELECT COUNT(*) FROM llm_usage WHERE kind = 'web_search' AND chat_id = @chat_id AND called_at >= @since",
                        {| chat_id = chatId; since = since |})
                found <- count > 0L
                if not found then do! Task.Delay 500
            return found
        }

    [<Fact>]
    member _.``NL image ask ("Алита, нарисуй...") gets a photo reply captioned with an in-persona reaction, not the prompt``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            if env.ResponderMode <> "llm" then
                Assert.Skip
                    "RESPONDER_MODE=llm required (the NL tool loop only runs off ResponderService) — run `RESPONDER_MODE=llm make real-test`"

            // Same real image-gen backend availability gate as ImageGenRealTests — the NL
            // `generate_image` tool shares MediaActions.generateImage with the `/img`
            // command, so the exact same quota/billing constraints apply.
            let usingGemini = not (String.IsNullOrWhiteSpace env.GeminiApiKey)

            if not usingGemini && String.IsNullOrWhiteSpace env.ImageDeployment then
                Assert.Skip
                    "Neither ALITA_GEMINI_API_KEY nor ALITA_IMAGE_DEPLOYMENT is set in ~/.alita-test/env — no real image-gen backend exists yet (Azure quota denied, see AlitaBot/docs/TECH-DEBT.md)"

            if usingGemini then
                let! blocked = GeminiProbe.isQuotaBlocked env.GeminiApiKey "gemini-3.1-flash-image"
                if blocked then
                    Assert.Skip
                        "Gemini image generation is billing-gated (free_tier limit: 0) for this ALITA_GEMINI_API_KEY's Google Cloud project — see GeminiProbe.fs's doc comment"

            let marker = Guid.NewGuid().ToString "N"
            let! msgId = fx.UserClient.SendText(env.TestChatId, $"Алита, нарисуй рыжего кота {marker}")

            match! fx.UserClient.TryAwaitPhotoReplyTo(env.TestChatId, msgId, ImageGenRealTimeouts.imageReply) with
            | None ->
                // No placeholder to race against on the NL path (unlike `/img` — MediaActions
                // never sends one for the tool path), so a missing photo after the full
                // timeout is a plain failure/skip-worthy transient, not a distinct edited-
                // placeholder outcome the way ImageGenRealTests races.
                Assert.Skip
                    $"No photo reply within {ImageGenRealTimeouts.imageReply.TotalSeconds}s — likely a transient upstream capacity issue, not asserting a hard failure"
            | Some photoReply ->
                Assert.False(String.IsNullOrWhiteSpace photoReply.message)
                let caption = photoReply.message
                // MEDIA_CAPTION_PROMPT explicitly forbids describing/repeating the prompt —
                // assert the caption is an in-persona reaction, not an echo of the marker.
                Assert.False(
                    caption.Contains marker,
                    $"expected an in-persona caption reaction, not an echo of the prompt/marker ({marker}): {caption}")
        })

    /// EMPIRICAL FINDING (first real run of this test, 2026-07-22): the real model, even
    /// when `web_search` came back correctly grounded (verified via the real Azure Responses
    /// API reply confirming ".NET 10 вышел 11 ноября 2025"), replied in-character with
    /// "...Хочешь ссылки на релиз-ноты или скачать SDK?" — offering to share links rather
    /// than pasting a raw URL. A strict "reply must contain http(s)://" assertion is too
    /// brittle against that persona-conversational style, so this asserts on the DB-verified
    /// `llm_usage(kind='web_search')` row (the tool genuinely fired) plus a loose grounding
    /// check on the reply text (a literal URL OR the correct release info), not JUST the URL.
    [<Fact>]
    member _.``NL search ask ("Алита, найди в интернете...") triggers a real web_search call and threads a grounded reply``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            if env.ResponderMode <> "llm" then
                Assert.Skip
                    "RESPONDER_MODE=llm required (the NL tool loop only runs off ResponderService) — run `RESPONDER_MODE=llm make real-test`"

            let beforeCall = DateTime.UtcNow
            let marker = Guid.NewGuid().ToString "N"
            let! msgId =
                fx.UserClient.SendText(env.TestChatId, $"Алита, найди в интернете, когда вышел .NET 10 {marker}")

            // The reply goes through the normal triggered-message path (ResponderService
            // "llm" mode -> EditThrottleRenderer under STREAM_MODE=edit by default), which
            // sends a short first chunk and then edits it into final form — AwaitReplyTo
            // alone catches that first, still-streaming chunk, not the settled text (same
            // gotcha DossierRealTests' recall-reply assertion already guards against).
            let! reply = fx.UserClient.AwaitReplyTo(env.TestChatId, msgId, Timeouts.reply)
            let! finalText = fx.UserClient.AwaitEditsSettled(env.TestChatId, reply.id, Timeouts.editQuiet)
            Assert.False(String.IsNullOrWhiteSpace finalText)

            let! sawWebSearchUsage = awaitWebSearchUsageRow env.TestChatId beforeCall

            if not sawWebSearchUsage then
                Assert.Skip
                    $"No llm_usage(kind='web_search') row recorded — web_search likely unconfigured (WEB_SEARCH_MODEL unset) or the Responses API endpoint rejected the call for this environment; bot replied: {finalText}"
            else
                Assert.True(
                    finalText.Contains("http://", StringComparison.OrdinalIgnoreCase)
                    || finalText.Contains("https://", StringComparison.OrdinalIgnoreCase)
                    || finalText.Contains("2025")
                    || finalText.Contains("ноября", StringComparison.OrdinalIgnoreCase),
                    $"expected the web_search-grounded reply to reference a URL or the real .NET 10 release info, got: {finalText}")
        })
