namespace AlitaBot.Services

open AlitaBot.Llm

/// One offerable tool's schema (S10 PR1) — declared once, pure data. `AdminOnly` tools
/// (PR2's `sql_query`) are never offered in a non-admin caller's tools array — the model
/// never even sees them exist, mirroring `/sql`'s own admin gate.
type ToolSpec =
    { Name: string
      Description: string
      ParametersJsonSchema: string
      AdminOnly: bool }

/// The tool catalog the NL tool-calling loop offers the model — pure data + filter, no
/// dispatch logic (see ToolExecutor.fs for that). PR1 ships two tools; PR2 adds
/// generate_song, speak_text, sql_query (AdminOnly), and a handful of read-only
/// thin-wrapper tools (ask_chat_history, summarize_chat, show_dossier, roast_user,
/// show_awards, show_quote, show_karma, switch_model, show_usage).
module ToolRegistry =
    let generateImage: ToolSpec =
        { Name = "generate_image"
          Description =
            "Generate/draw an image from a text prompt. Use this ONLY when the user "
            + "explicitly asks to draw or generate an image (e.g. «нарисуй», \"draw\"). If "
            + "the message you're replying to has a photo attached, that photo is AUTOMATICALLY "
            + "used as the edit source — never ask the user to attach or resend an image."
          ParametersJsonSchema =
            """{"type":"object","properties":{"prompt":{"type":"string","description":"What to draw, in the user's own words/language."}},"required":["prompt"]}"""
          AdminOnly = false }

    let webSearch: ToolSpec =
        { Name = "web_search"
          Description =
            "Search the public web for current or factual information that is not in your "
            + "training data or in this chat's history."
          ParametersJsonSchema =
            """{"type":"object","properties":{"query":{"type":"string","description":"The search query."}},"required":["query"]}"""
          AdminOnly = false }

    /// PR1's full tool set. PR2 appends more entries here.
    let all: ToolSpec list = [ generateImage; webSearch ]

    let toToolDef (t: ToolSpec) : ToolDef =
        { Name = t.Name
          Description = t.Description
          ParametersJsonSchema = t.ParametersJsonSchema }

    /// `isAdmin` filters AdminOnly tools out entirely (the model never sees `sql_query`,
    /// PR2). `webSearchEnabled` is `web_search`'s own per-tool kill switch
    /// (WEB_SEARCH_ENABLED) — `generate_image` instead reuses IMAGE_GEN_ENABLED, checked
    /// inside MediaActions.generateImage's own guardrails, not here.
    let availableToolDefs (webSearchEnabled: bool) (isAdmin: bool) : ToolDef list =
        all
        |> List.filter (fun t -> (not t.AdminOnly || isAdmin) && (t.Name <> "web_search" || webSearchEnabled))
        |> List.map toToolDef
