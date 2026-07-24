/// Reaction palette + choice-mode helpers shared by two INDEPENDENT reaction producers:
/// the message-level reaction roll (`BotService.tryReact` — `REACTION_PROBABILITY` +
/// `REACTION_COOLDOWN_SECONDS`, fires on ANY first-delivery message regardless of whether
/// it's addressed to the bot) and meme-react (`BotService.tryMemeReact`, photo-only).
/// Neither one competes with replying: a TRIGGERED message (mention, reply-to-bot, name
/// trigger) always gets the reply path now — see `BotService.handleTriggerableMessage`.
/// The old `OUTCOME_WEIGHTS`-driven reply/silence/emoji roll (Slice 6) that used to make
/// replying to a triggered message a coin flip has been removed entirely (redesign,
/// PR #253 follow-up) — `OUTCOME_WEIGHTS` is no longer read anywhere in the codebase.
module AlitaBot.Services.OutcomeRouter

open System.Text.Json

// ── Reaction palette (message-level reaction roll + S8 meme-react, hot-reloadable) ──
//
// Both the message-level reaction roll (above) and meme-react (BotService.tryMemeReact)
// pick a single emoji to hand `Req.SetMessageReaction`. Telegram only accepts
// `ReactionTypeEmoji` values from ITS OWN fixed allowed set — and rejects the WHOLE call
// if any one entry in the request isn't on that list — so REACTION_PALETTE (a
// bot_setting, letting the pool be tuned live) is validated against that set before ever
// reaching the wire.

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
/// (including "llm" and any unrecognized value — lenient parse, never a crash on a typo)
/// takes the LLM-pick path with a random fallback on failure.
[<Literal>]
let ModeRandom = "random"

[<Literal>]
let ModeLlm = "llm"

/// Deterministic index pick over `palette` given a uniform draw `roll` in [0, 1) — the
/// actual random draw lives at the call site (`Random.Shared.NextDouble()`), kept out of
/// this function so the pick itself is deterministic and testable. An empty palette
/// (should never happen — `parsePalette` never returns one) falls back to
/// `defaultPalette`'s first entry rather than throwing.
let pickRandomEmoji (palette: string[]) (roll: float) : string =
    if palette.Length = 0 then
        defaultPalette[0]
    else
        let idx = int ((max 0.0 (min roll 0.999999999)) * float palette.Length)
        palette[min idx (palette.Length - 1)]
