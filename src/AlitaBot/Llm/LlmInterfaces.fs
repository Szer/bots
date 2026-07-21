namespace AlitaBot.Llm

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

type IChatCompletion =
    abstract Complete : request: ChatRequest * ct: CancellationToken -> Task<Result<ChatResponse, LlmError>>
    abstract CompleteStream : request: ChatRequest * ct: CancellationToken -> IAsyncEnumerable<ChatChunk>

type IEmbeddings =
    abstract Embed : deployment: string * texts: string list * ct: CancellationToken -> Task<Result<float32[][], LlmError>>

// ── Multimodal ────────────────────────────────────────────────────────

/// Image generation (Phase-1 Slice 3): `sourceImage = None` calls Azure's
/// images/generations endpoint (text -> image); `Some bytes` calls images/edits
/// (img2img — a reply-to-a-photo `/img` prompt). Returns the generated image
/// bytes (PNG, decoded from the API's b64_json response) plus token usage —
/// gpt-image-1 bills by tokens the same way chat completions do, but the actual
/// USD cost is looked up per-image-per-quality-tier (see AlitaBot.Llm.ImagePricing),
/// not computed from these token counts.
type IImageGen =
    abstract Generate : prompt: string * sourceImage: byte[] option * ct: CancellationToken -> Task<Result<byte[] * TokenUsage, LlmError>>

// ── Multimodal stubs — implementations arrive in later phases ───────────

type IMusicGen =
    abstract Generate : prompt: string * ct: CancellationToken -> Task<Result<byte[], LlmError>>

type ISpeech =
    /// `voice` defaults to "alloy" when None.
    abstract Synthesize : text: string * voice: string option * ct: CancellationToken -> Task<Result<byte[], LlmError>>
    abstract Transcribe : audio: byte[] * ct: CancellationToken -> Task<Result<string, LlmError>>
