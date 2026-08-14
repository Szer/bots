module VahterBanBot.InjectionHeuristics

open System.Text.RegularExpressions

/// Conservative, phrasing-anchored detectors for prompt-injection attempts embedded in
/// untrusted user content (message text, OCR text, display name, bio, …) that ends up inside
/// LlmTriage's `<untrusted-*>` fenced blocks. These chats legitimately discuss AI/LLM topics,
/// so every pattern requires INSTRUCTION-SHAPED phrasing, not a bare topic mention — "system
/// prompt" as a noun phrase fires, mentioning "GPT" in casual conversation never would. Used
/// by LlmTriage to downgrade an LLM NOT_SPAM verdict to SKIP (human review) — never to force
/// SPAM/Kill on its own; see LlmTriage.fs's classifyUncached for the call site.
let private patterns : (string * Regex) list =
    [ "ignore_instructions",   Regex(@"ignore (all |the )?(previous|above|prior) instructions", RegexOptions.IgnoreCase ||| RegexOptions.Compiled)
      "disregard_prompt",      Regex(@"disregard (your|the) (instructions|prompt)", RegexOptions.IgnoreCase ||| RegexOptions.Compiled)
      "role_override",         Regex(@"you are now", RegexOptions.IgnoreCase ||| RegexOptions.Compiled)
      "new_instructions",      Regex(@"new instructions:", RegexOptions.IgnoreCase ||| RegexOptions.Compiled)
      "system_prompt_mention", Regex(@"system prompt", RegexOptions.IgnoreCase ||| RegexOptions.Compiled)
      "respond_with",          Regex(@"respond (only )?with", RegexOptions.IgnoreCase ||| RegexOptions.Compiled)
      "verdict_literal",       Regex(@"\{""verdict""|NOT_SPAM", RegexOptions.IgnoreCase ||| RegexOptions.Compiled)
      "addressing_classifier", Regex(@"as an ai|as a language model|to the (ai|llm|classifier|moderator bot)", RegexOptions.IgnoreCase ||| RegexOptions.Compiled) ]

/// Returns the name of the first matching pattern, or `None`. Never throws — a null/empty
/// input is simply "no match", the same as any other miss.
let detect (text: string) : string option =
    if System.String.IsNullOrEmpty text then None
    else patterns |> List.tryFind (fun (_, re) -> re.IsMatch text) |> Option.map fst
