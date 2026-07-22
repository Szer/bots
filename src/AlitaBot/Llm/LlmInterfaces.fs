namespace AlitaBot.Llm

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

type IChatCompletion =
    abstract Complete : request: ChatRequest * ctx: UsageContext * ct: CancellationToken -> Task<Result<ChatResponse, LlmError>>
    abstract CompleteStream : request: ChatRequest * ctx: UsageContext * ct: CancellationToken -> IAsyncEnumerable<ChatChunk>

type IEmbeddings =
    abstract Embed : deployment: string * texts: string list * ctx: UsageContext * ct: CancellationToken -> Task<Result<float32[][], LlmError>>

/// Sink for the `llm_usage` table (Phase-1 Slice 4) — implemented by
/// AlitaBot.Services.DbService, injected into the provider layer (AzureFoundryProvider.fs)
/// so LlmCall/ImageCall (LlmTelemetry.fs) can persist a row alongside the existing OTel
/// metrics on every successful call. `kind` is one of the `llm_usage.kind` CHECK values
/// ("chat" | "stt" | "tts" | "image" | "embedding" | "music" — the last added by the Gemini
/// slice's V7 migration). Fire-and-forget at the call site — a slow/failed usage-row insert
/// must never hold up or fail the actual bot reply.
type IUsageRecorder =
    abstract Record :
        kind: string *
        model: string *
        inputTokens: int option *
        outputTokens: int option *
        costUsd: float option *
        ctx: UsageContext ->
            Task<unit>

// ── Multimodal ────────────────────────────────────────────────────────

/// Image generation (Phase-1 Slice 3): `sourceImage = None` calls Azure's
/// images/generations endpoint (text -> image); `Some bytes` calls images/edits
/// (img2img — a reply-to-a-photo `/img` prompt). Returns the generated image
/// bytes (PNG, decoded from the API's b64_json response) plus token usage —
/// gpt-image-1 bills by tokens the same way chat completions do, but the actual
/// USD cost is looked up per-image-per-quality-tier (see AlitaBot.Llm.ImagePricing),
/// not computed from these token counts.
type IImageGen =
    abstract Generate : prompt: string * sourceImage: byte[] option * ctx: UsageContext * ct: CancellationToken -> Task<Result<byte[] * TokenUsage, LlmError>>

/// Music generation (Gemini/Lyria slice): `prompt` is the caller-assembled style + lyrics
/// text (see BotService's `/song` — an optional leading "(style hint)" is folded in before
/// this is called). Returns the generated audio bytes (whatever container Lyria's
/// generateContent response carries — see GeminiProvider.fs's doc comments; BotService
/// re-encodes/falls back the same way `/say`'s ISpeech.Synthesize output does) plus token
/// usage, mirroring IImageGen's shape so the same telemetry/pricing idioms apply.
type IMusicGen =
    abstract Generate : prompt: string * ctx: UsageContext * ct: CancellationToken -> Task<Result<byte[] * TokenUsage, LlmError>>

type ISpeech =
    /// `voice` defaults to "alloy" when None.
    abstract Synthesize : text: string * voice: string option * ctx: UsageContext * ct: CancellationToken -> Task<Result<byte[], LlmError>>
    abstract Transcribe : audio: byte[] * ctx: UsageContext * ct: CancellationToken -> Task<Result<string, LlmError>>
