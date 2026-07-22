/// Weighted outcome roll for a TRIGGERED non-command message (Slice 6): normally the bot
/// just replies, but `OUTCOME_WEIGHTS` (bot_setting, JSON_BLOB) lets it also roll
/// "silence" (say nothing) or "emoji" (react instead of replying) — tunable in prod
/// without a redeploy, defaulting to the pre-S6 always-reply behavior.
module AlitaBot.Services.OutcomeRouter

open System.Text.Json

/// Parsed `OUTCOME_WEIGHTS` — non-negative integer weights for each outcome. Missing keys
/// (or a malformed value/whole JSON) fall back to the always-reply default per key, never
/// a crash or a silently empty roll.
type Weights =
    { Reply: int
      Silence: int
      Emoji: int }

let defaultWeights = { Reply = 100; Silence = 0; Emoji = 0 }

/// Lenient parse of `{"reply":100,"silence":0,"emoji":0}` — any missing/non-numeric field
/// falls back to `defaultWeights`'s value for that field; malformed JSON overall falls
/// back to `defaultWeights` entirely.
let parseWeights (json: string) : Weights =
    try
        use doc = JsonDocument.Parse(json: string)
        let intOr (key: string) (def: int) =
            match doc.RootElement.TryGetProperty key with
            | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
            | _ -> def
        { Reply = intOr "reply" defaultWeights.Reply
          Silence = intOr "silence" defaultWeights.Silence
          Emoji = intOr "emoji" defaultWeights.Emoji }
    with _ ->
        defaultWeights

/// One of "reply" | "silence" | "emoji".
[<Literal>]
let Reply = "reply"

[<Literal>]
let Silence = "silence"

[<Literal>]
let Emoji = "emoji"

/// Weighted pick over {reply, silence, emoji} given a uniform draw `roll` in [0, 1) — the
/// actual random draw lives at the call site (`Random.Shared.NextDouble()`), kept out of
/// this function so the pick itself is deterministic and testable. Negative weights are
/// clamped to 0; an all-zero (or entirely non-positive) total NEVER silently drops the
/// trigger — it degrades to "reply", the same posture as `defaultWeights`. Boundaries are
/// half-open ([0, replyW) -> reply, [replyW, replyW+silenceW) -> silence, rest -> emoji),
/// each candidate's own share is not entered when its weight is 0 (`r < replyW` false when
/// replyW = 0 and r = 0), so a 0-weighted outcome can never be picked even at the roll's
/// very edge.
let pick (weights: Weights) (roll: float) : string =
    let replyW = float (max 0 weights.Reply)
    let silenceW = float (max 0 weights.Silence)
    let emojiW = float (max 0 weights.Emoji)
    let total = replyW + silenceW + emojiW
    if total <= 0.0 then
        Reply
    else
        let r = (max 0.0 (min roll 0.999999999)) * total
        if r < replyW then Reply
        elif r < replyW + silenceW then Silence
        else Emoji
