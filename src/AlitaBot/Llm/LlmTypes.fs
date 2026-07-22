namespace AlitaBot.Llm

open System

[<RequireQualifiedAccess>]
type ChatRole =
    | System
    | User
    | Assistant
    | Tool

[<RequireQualifiedAccess>]
type ContentPart =
    | Text of string
    /// `detail` is OpenAI's image_url detail hint ("low" | "high"); None omits the
    /// field entirely (server default). See BotConfiguration.VisionDetail.
    | ImageUrl of url: string * detail: string option

type ToolCall =
    { Id: string
      Name: string
      ArgumentsJson: string }

type ChatMessage =
    { Role: ChatRole
      Content: ContentPart list
      /// Set on Assistant messages that requested tool calls.
      ToolCalls: ToolCall list
      /// Set on Tool messages: the id of the call being answered.
      ToolCallId: string option }

type ToolDef =
    { Name: string
      Description: string
      ParametersJsonSchema: string }

type ChatRequest =
    { Deployment: string
      Messages: ChatMessage list
      Tools: ToolDef list
      Temperature: float option
      MaxTokens: int option
      /// gpt-5-family "reasoning_effort" ("minimal" | "low" | "medium" | "high") — omitted
      /// entirely when None (server default, currently observed to reason heavily even on
      /// tiny formulaic asks). PROD INCIDENT (S10 first live use, 2026-07-22 staging): with
      /// this left unset, MediaActions.composeCaption's call — a big persona SystemPrompt
      /// plus a one-line "react in 1-2 sentences" ask — burned its ENTIRE
      /// `max_completion_tokens` budget on reasoning tokens with finish_reason="length" and
      /// ZERO visible text, even after raising the budget to 500 (confirmed against a real
      /// Azure call, not simulated). Only Azure Foundry's chat-completions wire
      /// (AzureWire.buildChatBody) currently reads this — Gemini/Responses-API providers
      /// don't implement IChatCompletion.
      ReasoningEffort: string option }

type TokenUsage =
    { PromptTokens: int
      CompletionTokens: int
      TotalTokens: int }

[<RequireQualifiedAccess>]
type FinishReason =
    | Stop
    | Length
    | ToolCalls
    | ContentFilter

type ChatResponse =
    { Text: string
      ToolCalls: ToolCall list
      FinishReason: FinishReason
      Usage: TokenUsage option }

[<RequireQualifiedAccess>]
type LlmError =
    | RateLimited of retryAfter: TimeSpan option
    | ContentFiltered of detail: string
    | ApiError of status: int * body: string
    | NetworkError of message: string

/// Companion module (RequireQualifiedAccess on the type itself already forces
/// `LlmError.RateLimited`-style access to its cases, so `LlmError.isTransient` matches
/// that same qualified convention for callers).
module LlmError =
    /// Whether an `LlmError` is likely transient — worth telling the user "try again
    /// shortly" instead of the generic failure shrug (prod evidence: /img failing on a
    /// Gemini `503 UNAVAILABLE` "high demand" response with no indication to the user
    /// that retrying might just work). `RateLimited` (429-class, either provider) always
    /// counts; `ApiError` only counts when the status/body carry Gemini's
    /// `503`/`"UNAVAILABLE"` shape — Azure's 5xx bodies don't share that vocabulary, and
    /// a bare "5xx" alone isn't a reliable enough transient signal to promise a retry
    /// will help. `ContentFiltered`/`NetworkError` are never transient in this sense:
    /// retrying won't un-block a filtered prompt, and a network error already reads as
    /// "something's wrong", not "try again".
    let isTransient (err: LlmError) : bool =
        match err with
        | LlmError.RateLimited _ -> true
        | LlmError.ApiError(status, body) ->
            status = 503 || body.Contains("UNAVAILABLE", StringComparison.OrdinalIgnoreCase)
        | LlmError.ContentFiltered _ -> false
        | LlmError.NetworkError _ -> false

/// Chat/user attribution threaded through every provider call (Complete/CompleteStream/
/// Embed/Transcribe/Synthesize/Generate) so the persisted `llm_usage` row (Phase-1 Slice 4)
/// can be attributed — None fields become NULL columns (e.g. a call with no natural chat/user
/// context). Purely additive: the existing OTel metrics don't use this.
type UsageContext =
    { ChatId: int64 option
      UserId: int64 option }
    static member None = { ChatId = None; UserId = None }

/// One element of a streamed completion. Errors flow in-band via `Failed` —
/// the stream itself never throws.
[<RequireQualifiedAccess>]
type ChatChunk =
    /// Incremental text delta.
    | TextDelta of string
    /// Partial tool-call fragment; `index` identifies the call being assembled.
    /// The fully assembled calls arrive in `Completed`.
    | ToolCallDelta of index: int * id: string option * name: string option * argsDelta: string
    /// Terminal: the fully assembled response.
    | Completed of ChatResponse
    /// Terminal: the stream failed.
    | Failed of LlmError
