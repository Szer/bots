namespace CouponHubBot.RealTests

open System.Threading.Tasks

/// COPIED from tests/AlitaBot.RealTests/TestRetry.fs (see TgUserClient.fs's doc comment
/// for why this is a copy, not a shared reference).
///
/// Suite-wide anti-flake policy: every real test gets exactly ONE automatic retry, and
/// ONLY for TIMEOUT-class failures — an AwaitTimeoutException raised by one of
/// TgUserClient's Await* helpers (the "No message matching ... within Ns" family).
/// Genuine assertion failures (Assert.*, Assert.Fail) are a different, non-flaky
/// failure mode and propagate on the first attempt, uncaught — never retry an
/// assertion failure (contract, "Photo fixture" / test-coverage section).
///
/// `body` is a thunk, not a pre-built Task, so retrying means CALLING it again: a test
/// that adds a fresh coupon / sends a fresh Telegram message inside `body` gets fresh
/// correlation on attempt 2. Test names/reporting are unaffected: this wraps the body,
/// it doesn't touch [<Fact>] discovery, so a retried-then-passing test still reports as
/// a single green test (with a "[retry]" line in its output for visibility).
module TestRetry =
    let withTimeoutRetry (body: unit -> Task) : Task =
        task {
            try
                do! body ()
            with :? AwaitTimeoutException as ex ->
                printfn "[retry] attempt 1 timed out (%s) — retrying once" ex.Message
                do! body ()
        }
