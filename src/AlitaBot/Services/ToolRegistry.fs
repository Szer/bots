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

    // ── PR2: generate_song, speak_text, sql_query (AdminOnly), read-only tools ─────────

    let generateSong: ToolSpec =
        { Name = "generate_song"
          Description =
            "Compose/generate a short song (music + lyrics) — use ONLY when explicitly asked "
            + "to compose/sing something (e.g. «сочини песню», «спой про», \"write a song\"). "
            + "Never use this just to speak plain text out loud — that's speak_text."
          ParametersJsonSchema =
            """{"type":"object","properties":{"style":{"type":"string","description":"Optional music style/genre hint, e.g. \"рок-баллада\"."},"lyrics_or_description":{"type":"string","description":"The lyrics to sing, or a description of what the song should be about, in the user's own words/language."}},"required":["lyrics_or_description"]}"""
          AdminOnly = false }

    let speakText: ToolSpec =
        { Name = "speak_text"
          Description =
            "Speak text out loud as a voice message (text-to-speech) — use ONLY when explicitly "
            + "asked to say/voice something out loud (e.g. «скажи это голосом», «озвучь», "
            + "\"say this out loud\"). Never use this for a normal text reply."
          ParametersJsonSchema =
            """{"type":"object","properties":{"text":{"type":"string","description":"The exact text to speak."},"voice":{"type":"string","description":"Optional voice name: alloy, ash, ballad, coral, echo, fable, nova, onyx, sage, shimmer, or verse. Omit to use the default voice."}},"required":["text"]}"""
          AdminOnly = false }

    let sqlQuery: ToolSpec =
        { Name = "sql_query"
          Description =
            "Run a read-only, natural-language-described SQL analytics query against the bot's "
            + "own database (message/usage statistics etc.) — ADMIN ONLY. Use when an admin asks "
            + "a data question that needs querying the database directly, e.g. «сколько сообщений "
            + "было вчера», «топ пользователей по активности»."
          ParametersJsonSchema =
            """{"type":"object","properties":{"question":{"type":"string","description":"The analytics question, in natural language."}},"required":["question"]}"""
          AdminOnly = true }

    let askChatHistory: ToolSpec =
        { Name = "ask_chat_history"
          Description =
            "Answer a question by semantically searching this chat's message history for "
            + "relevant past messages — use when asked about something discussed earlier that "
            + "isn't in the current context, e.g. «когда мы договорились встретиться», "
            + "«что говорили про отпуск»."
          ParametersJsonSchema =
            """{"type":"object","properties":{"question":{"type":"string","description":"The question to answer from chat history."}},"required":["question"]}"""
          AdminOnly = false }

    let summarizeChat: ToolSpec =
        { Name = "summarize_chat"
          Description =
            "Summarize the recent conversation in this chat — use when explicitly asked for a "
            + "recap/summary/TL;DR, e.g. «перескажи, что тут обсуждали», «краткое содержание»."
          ParametersJsonSchema =
            """{"type":"object","properties":{"count":{"type":"integer","description":"How many recent messages to summarize. Omit for the default."}},"required":[]}"""
          AdminOnly = false }

    let showDossier: ToolSpec =
        { Name = "show_dossier"
          Description =
            "Show what you (Алита) know/remember about a person from your accumulated dossier — "
            + "use when asked what you know about someone or yourself, e.g. «что ты обо мне "
            + "знаешь», «что ты знаешь про Ивана»."
          ParametersJsonSchema =
            """{"type":"object","properties":{"target":{"type":"string","description":"@username of the target, or omit/empty for the person asking."}},"required":[]}"""
          AdminOnly = false }

    let roastUser: ToolSpec =
        { Name = "roast_user"
          Description =
            "Roast (playfully insult) a person using what you know about them — use ONLY when "
            + "explicitly asked to roast/прожарь someone, e.g. «прожарь меня», «прожарь Ивана»."
          ParametersJsonSchema =
            """{"type":"object","properties":{"target":{"type":"string","description":"@username of the target, or omit/empty to roast the person asking."}},"required":[]}"""
          AdminOnly = false }

    let showAwards: ToolSpec =
        { Name = "show_awards"
          Description =
            "Hand out this week's joke \"awards\" to chat members based on the last week's "
            + "conversation — use when asked for weekly awards/highlights, e.g. «раздай награды "
            + "недели», «кто заслужил награду на этой неделе»."
          ParametersJsonSchema = """{"type":"object","properties":{},"required":[]}"""
          AdminOnly = false }

    let showQuote: ToolSpec =
        { Name = "show_quote"
          Description =
            "Pick the most absurd/quotable line from the last 24 hours of this chat — use when "
            + "asked for the quote of the day, e.g. «цитата дня», «что смешного сегодня писали»."
          ParametersJsonSchema = """{"type":"object","properties":{},"required":[]}"""
          AdminOnly = false }

    let showKarma: ToolSpec =
        { Name = "show_karma"
          Description =
            "Show someone's karma/awards total — use when asked about karma, e.g. «сколько у "
            + "меня кармы», «какая карма у Ивана»."
          ParametersJsonSchema =
            """{"type":"object","properties":{"target":{"type":"string","description":"@username of the target, or omit/empty for the person asking."}},"required":[]}"""
          AdminOnly = false }

    let switchModel: ToolSpec =
        { Name = "switch_model"
          Description =
            "Show the current/available LLM models, or switch the active one — use when asked "
            + "what model you're running, or to switch models, e.g. «какая ты модель», "
            + "«переключись на gpt-5»."
          ParametersJsonSchema =
            """{"type":"object","properties":{"model":{"type":"string","description":"Exact model name to switch to (must match the catalog exactly). Omit to just show the current model + catalog."}},"required":[]}"""
          AdminOnly = false }

    let showUsage: ToolSpec =
        { Name = "show_usage"
          Description =
            "Show LLM usage/cost statistics (today + last 7 days) — use when asked about usage, "
            + "cost, or spend, e.g. «сколько потратили на тебя», «покажи статистику "
            + "использования»."
          ParametersJsonSchema = """{"type":"object","properties":{},"required":[]}"""
          AdminOnly = false }

    /// The full tool catalog (PR1 + PR2).
    let all: ToolSpec list =
        [ generateImage
          webSearch
          generateSong
          speakText
          sqlQuery
          askChatHistory
          summarizeChat
          showDossier
          roastUser
          showAwards
          showQuote
          showKarma
          switchModel
          showUsage ]

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
