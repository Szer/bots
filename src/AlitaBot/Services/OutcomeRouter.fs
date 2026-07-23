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

// ── Reaction palette (S6 emoji outcome + S8 meme-react, made hot-reloadable) ────────
//
// Both the emoji outcome (above) and meme-react (BotService.tryMemeReact) pick a single
// emoji to hand `Req.SetMessageReaction`. Telegram only accepts `ReactionTypeEmoji` values
// from ITS OWN fixed allowed set — and rejects the WHOLE call if any one entry in the
// request isn't on that list — so REACTION_PALETTE (a bot_setting, letting the pool be
// tuned live) is validated against that set before ever reaching the wire.

/// Telegram's documented set of emoji `setMessageReaction`'s `ReactionTypeEmoji` accepts
/// (https://core.telegram.org/bots/api#reactiontypeemoji, "emoji" field) — anything outside
/// this set is invalid and gets filtered out of REACTION_PALETTE rather than sent
/// unchecked. Kept as plain code points exactly as Telegram documents them (e.g. "❤", not
/// "❤️" with a trailing variation selector — the two are visually identical but are
/// different strings, and only the former is on Telegram's list).
let telegramAllowedReactionEmoji: Set<string> =
    set [
        "👍"; "👎"; "❤"; "🔥"; "🥰"; "👏"; "😁"; "🤔"; "🤯"; "😱"; "🤬"; "😢"; "🎉"; "🤩"; "🤮"
        "💩"; "🙏"; "👌"; "🕊"; "🤡"; "🥱"; "🥴"; "😍"; "🐳"; "❤‍🔥"; "🌚"; "🌭"; "💯"; "🤣"; "⚡"
        "🍌"; "🏆"; "💔"; "🤨"; "😐"; "🍓"; "🍾"; "💋"; "🖕"; "😈"; "😴"; "😭"; "🤓"; "👻"; "👨‍💻"
        "👀"; "🎃"; "🙈"; "😇"; "😨"; "🤝"; "✍"; "🤗"; "🫡"; "🎅"; "🎄"; "☃"; "💅"; "🤪"; "🗿"
        "🆒"; "💘"; "🙉"; "🦄"; "😘"; "💊"; "🙊"; "😎"; "👾"; "🤷‍♂"; "🤷"; "🤷‍♀"; "😡"
    ]

/// The pre-hot-reload hardcoded palette (S6/S8's original `BotService.allowedReactionEmoji`)
/// — used when REACTION_PALETTE is missing, malformed, or filters down to empty. Every
/// entry is independently a member of `telegramAllowedReactionEmoji`.
let defaultPalette: string[] =
    [| "👍"; "❤"; "🔥"; "😁"; "🤔"; "🤯"; "😱"; "🤬"; "😢"; "🎉"; "🤩"; "💩"; "🤡"; "🥱" |]

/// Parses REACTION_PALETTE (a JSON array of emoji strings), keeping only entries that are
/// members of `telegramAllowedReactionEmoji` (order-preserving, de-duplicated). Malformed
/// JSON, a non-array root, or a palette that filters down to empty all fall back to
/// `defaultPalette` — a reaction always has SOMETHING valid to pick from. Returns the
/// filtered-out entries alongside the usable palette so the caller (which owns the
/// `ILogger`, unlike this pure module) can Warning-log them.
let parsePalette (json: string) : string[] * string list =
    let fallback = defaultPalette, []
    try
        use doc = JsonDocument.Parse(json: string)
        if doc.RootElement.ValueKind <> JsonValueKind.Array then
            fallback
        else
            let entries =
                doc.RootElement.EnumerateArray()
                |> Seq.choose (fun v -> if v.ValueKind = JsonValueKind.String then Some(v.GetString()) else None)
                |> Seq.toList
            let valid = entries |> List.filter telegramAllowedReactionEmoji.Contains |> List.distinct
            let invalid = entries |> List.filter (telegramAllowedReactionEmoji.Contains >> not)
            if valid.IsEmpty then defaultPalette, invalid else List.toArray valid, invalid
    with _ ->
        fallback

/// REACTION_CHOICE_MODE values: "random" skips the LLM call entirely; anything else
/// (including "llm" and any unrecognized value — lenient, same posture as OUTCOME_WEIGHTS)
/// takes the LLM-pick path with a random fallback on failure.
[<Literal>]
let ModeRandom = "random"

[<Literal>]
let ModeLlm = "llm"

/// Deterministic index pick over `palette` given a uniform draw `roll` in [0, 1) — the
/// actual random draw lives at the call site (`Random.Shared.NextDouble()`), same
/// separation-of-concerns as `pick` above. An empty palette (should never happen —
/// `parsePalette` never returns one) falls back to `defaultPalette`'s first entry rather
/// than throwing.
let pickRandomEmoji (palette: string[]) (roll: float) : string =
    if palette.Length = 0 then
        defaultPalette[0]
    else
        let idx = int ((max 0.0 (min roll 0.999999999)) * float palette.Length)
        palette[min idx (palette.Length - 1)]
