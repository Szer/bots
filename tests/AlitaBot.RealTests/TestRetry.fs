namespace AlitaBot.RealTests

open System.Threading.Tasks

/// Suite-wide anti-flake policy for tests/AlitaBot.RealTests: every real test gets
/// exactly ONE automatic retry, and ONLY for TIMEOUT-class failures — an
/// AwaitTimeoutException raised by one of TgUserClient's Await* helpers (the "No ...
/// within Ns" family). Genuine assertion failures (Assert.*, Assert.Fail) are a
/// different, non-flaky failure mode and propagate on the first attempt, uncaught.
///
/// `body` is a thunk, not a pre-built Task, so retrying means CALLING it again: a
/// test that mints a fresh GUID marker / sends a fresh Telegram message inside
/// `body` gets fresh correlation on attempt 2 — attempt 2 never matches attempt 1's
/// stale traffic in the shared long-lived test chat (see TgUserClient.fs). Test
/// names/reporting are unaffected: this wraps the body, it doesn't touch [<Fact>]
/// discovery, so a retried-then-passing test still reports as a single green test
/// (with a "[retry]" line in its output for visibility).
///
/// NOTE: VoiceRealTests deliberately keeps its own, pre-existing hand-rolled retry
/// (any-of-3-words STT flake tolerance) instead of this wrapper — that retry is
/// intentionally broader (any exception, not just timeouts) to also absorb
/// misrecognition-class assertion failures, a documented exception to this policy.
module TestRetry =
    let withTimeoutRetry (body: unit -> Task) : Task =
        task {
            try
                do! body ()
            with :? AwaitTimeoutException as ex ->
                printfn "[retry] attempt 1 timed out (%s) — retrying once" ex.Message
                do! body ()
        }
