module VahterBanBot.LlmVerdictCache

open System
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open Npgsql
open Dapper
open VahterBanBot.SpamTextCache

/// A previously-computed LLM verdict, served from cache instead of re-calling Azure.
type CachedVerdict =
    { Verdict:   string
      Reason:    string option
      Source: (string * string) option
      PolicyHash: string option
      CreatedAt: DateTime }

/// MD5 of the NORMALIZED text (D5, SpamTextCache.normalize) — shared by LlmTriage.fs (builds
/// keys) and Bot.fs's correction handlers (invalidates them, D2), so both always agree.
let textHash (text: string) : string =
    MD5.HashData(Encoding.UTF8.GetBytes(normalize text)) |> Convert.ToHexString |> _.ToLower()

/// Global cache key for SPAM/SKIP text verdicts.
let globalKey (text: string) : string = sprintf "text:global:%s" (textHash text)

/// Sender-scoped cache key for SPAM/SKIP when global caching is disabled.
let senderKey (senderId: int64) (text: string) : string = sprintf "text:%d:%s" senderId (textHash text)

[<CLIMutable>]
type private LlmVerdictCacheRow = { verdict: string; reason: string | null; model_name: string | null; prompt_hash: string | null; policy_hash: string | null; created_at: DateTime }

[<AllowNullLiteral>]
type ILlmVerdictCache =
    /// Returns the cached verdict for this key if it was stored within `maxAge`, else None.
    abstract member TryGet: cacheKey: string * maxAge: TimeSpan -> Task<CachedVerdict option>
    /// Stores (or refreshes) the verdict for this key. Only SPAM/SKIP verdicts should be cached.
    abstract member Save: cacheKey: string * verdict: string * reason: string option * modelName: string option * promptHash: string * policyHash: string -> Task<unit>
    /// Deletes the row for `cacheKey`, optionally only when it currently holds `verdict` (None =
    /// any). Idempotent — a missing/non-matching row is not an error (D2 human-correction invalidation).
    abstract member Invalidate: cacheKey: string * verdict: string option -> Task<unit>

type LlmVerdictCacheRepository(connString: string) =
    interface ILlmVerdictCache with
        member _.TryGet(cacheKey, maxAge) = task {
            use conn = new NpgsqlConnection(connString)

            //language=postgresql
            let sql =
                """
SELECT verdict, reason, model_name, prompt_hash, policy_hash, created_at FROM llm_verdict_cache
WHERE cache_key = @cacheKey AND created_at > NOW() - make_interval(secs => @maxAgeSeconds)
                """

            let! rows =
                conn.QueryAsync<LlmVerdictCacheRow>(
                    sql, {| cacheKey = cacheKey; maxAgeSeconds = maxAge.TotalSeconds |})
            return
                rows
                |> Seq.tryHead
                |> Option.map (fun r ->
                    { Verdict   = r.verdict
                      Reason    = Option.ofObj r.reason
                      Source = Option.map2 (fun model prompt -> model, prompt) (Option.ofObj r.model_name) (Option.ofObj r.prompt_hash)
                      PolicyHash = Option.ofObj r.policy_hash
                      CreatedAt = r.created_at })
        }

        member _.Save(cacheKey, verdict, reason, modelName, promptHash, policyHash) = task {
            use conn = new NpgsqlConnection(connString)

            //language=postgresql
            let sql =
                """
INSERT INTO llm_verdict_cache (cache_key, verdict, reason, model_name, prompt_hash, policy_hash)
VALUES (@cacheKey, @verdict, @reason, @modelName, @promptHash, @policyHash)
ON CONFLICT (cache_key) DO UPDATE
    SET verdict = EXCLUDED.verdict, reason = EXCLUDED.reason,
        model_name = EXCLUDED.model_name, prompt_hash = EXCLUDED.prompt_hash,
        policy_hash = EXCLUDED.policy_hash, created_at = NOW()
                """

            let! _ =
                conn.ExecuteAsync(
                    sql,
                    {| cacheKey  = cacheKey
                       verdict   = verdict
                       reason    = Option.toObj reason
                       modelName = Option.toObj modelName
                       promptHash = promptHash
                       policyHash = policyHash |})
            return ()
        }

        member _.Invalidate(cacheKey, verdict) = task {
            use conn = new NpgsqlConnection(connString)

            //language=postgresql
            let sql =
                """
DELETE FROM llm_verdict_cache
WHERE cache_key = @cacheKey AND (@verdict IS NULL OR verdict = @verdict)
                """

            let! _ = conn.ExecuteAsync(sql, {| cacheKey = cacheKey; verdict = Option.toObj verdict |})
            return ()
        }
