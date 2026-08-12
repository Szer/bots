/// Pure unit coverage for `LlmTriage.isContentFilterRejection`, extracted specifically so the
/// Azure content_filter detection logic is testable without constructing a real
/// `ClientResultException` (see LlmTriage.fs's "content_filter detection" section). The
/// black-box behavior (an actual scripted 400 driving LlmVerdict.ContentFiltered ->
/// AutoVerdict.Spam) is covered separately in VahterBanBot.Tests/LlmContentFilterTests.fs.
module VahterBanBot.Unit.Tests.ContentFilterDetectionTests

open VahterBanBot.LlmTriage
open Xunit

let private realContentFilterBody =
    """{"error":{"message":"The response was filtered due to the prompt triggering Azure OpenAI's content management policy.","type":null,"param":"prompt","code":"content_filter","status":400,"innererror":{"code":"ResponsibleAIPolicyViolation","content_filter_result":{"sexual":{"filtered":true,"severity":"high"}}}}}"""

[<Fact>]
let ``400 with error.code=content_filter in the raw body is detected`` () =
    Assert.True(isContentFilterRejection 400 (Some realContentFilterBody) "irrelevant message")

[<Fact>]
let ``400 with a different error.code in the raw body is NOT detected`` () =
    let body = """{"error":{"message":"bad request","code":"invalid_request_error"}}"""
    Assert.False(isContentFilterRejection 400 (Some body) "irrelevant message")

[<Fact>]
let ``429 is never detected as content_filter even if the body mentions it`` () =
    Assert.False(isContentFilterRejection 429 (Some realContentFilterBody) "irrelevant message")

[<Fact>]
let ``5xx is never detected as content_filter`` () =
    let body = """{"error":{"code":"content_filter"}}"""
    Assert.False(isContentFilterRejection 500 (Some body) "irrelevant message")

[<Fact>]
let ``raw body unavailable: falls back to substring-matching the exception message`` () =
    Assert.True(isContentFilterRejection 400 None "Service rejected the request due to content_filter (status 400)")

[<Fact>]
let ``raw body unavailable and message does not mention content_filter: NOT detected`` () =
    Assert.False(isContentFilterRejection 400 None "Bad request: missing required field")

[<Fact>]
let ``raw body present but unparsable: does NOT fall back to the message match`` () =
    // Prefers the raw body when it exists — a body that fails to parse is treated as "no code
    // found", not silently promoted to a message-substring guess.
    Assert.False(isContentFilterRejection 400 (Some "not json") "this message mentions content_filter")
