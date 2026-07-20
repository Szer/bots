namespace AlitaBot.Llm

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

type IChatCompletion =
    abstract Complete : request: ChatRequest * ct: CancellationToken -> Task<Result<ChatResponse, LlmError>>
    abstract CompleteStream : request: ChatRequest * ct: CancellationToken -> IAsyncEnumerable<ChatChunk>

type IEmbeddings =
    abstract Embed : deployment: string * texts: string list * ct: CancellationToken -> Task<Result<float32[][], LlmError>>

// ── Multimodal stubs — implementations arrive in later phases ───────────

type IImageGen =
    abstract Generate : prompt: string * ct: CancellationToken -> Task<Result<byte[], LlmError>>

type IMusicGen =
    abstract Generate : prompt: string * ct: CancellationToken -> Task<Result<byte[], LlmError>>

type ISpeech =
    abstract Synthesize : text: string * ct: CancellationToken -> Task<Result<byte[], LlmError>>
    abstract Transcribe : audio: byte[] * ct: CancellationToken -> Task<Result<string, LlmError>>
