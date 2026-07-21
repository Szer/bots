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
    | ImageUrl of url: string

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
      MaxTokens: int option }

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
