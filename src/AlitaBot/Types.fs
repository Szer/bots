namespace AlitaBot

[<CLIMutable>]
type BotConfiguration =
    { BotToken: string
      SecretToken: string
      TelegramApiBaseUrl: string | null
      /// Chats the bot listens to; everything else is ignored silently.
      TargetChatIds: int64 list
      BotUsername: string
      SystemPrompt: string
      AzureFoundryEndpoint: string
      AzureFoundryKey: string
      LlmDeployment: string
      EmbeddingDeployment: string
      /// Speech-to-text deployment name (Azure Foundry audio/transcriptions).
      SttDeployment: string
      /// Text-to-speech deployment name (Azure Foundry audio/speech).
      TtsDeployment: string
      /// LLM_PRICING bot_setting: {"gpt-5-mini":{"input_per_1m":0.25,"output_per_1m":2.00}}
      LlmPricingJson: string
      /// "echo" | "llm"
      ResponderMode: string
      /// "draft" | "edit" | "plain"
      StreamMode: string
      ContextWindowMessages: int
      /// FEATURE_FLAG VOICE_TRANSCRIBE_ENABLED — auto-transcribe Voice/VideoNote/Audio messages in target chats. Default true.
      VoiceTranscribeEnabled: bool
      /// FEATURE_FLAG VISION_ENABLED — attach photos (the triggering message's own, and/or
      /// its reply target's) as image_url parts on LLM requests. Default true.
      VisionEnabled: bool
      /// VISION_DETAIL bot_setting: OpenAI image_url detail hint ("low" | "high"), controls
      /// vision token cost. Default "low".
      VisionDetail: string
      /// Image-generation deployment name (Azure Foundry images/generations + images/edits).
      /// Empty when unconfigured (e.g. quota denied at deploy time) — /img then fails
      /// gracefully with a RU error reply instead of crashing.
      ImageDeployment: string
      /// FEATURE_FLAG IMAGE_GEN_ENABLED — the /img and !img commands. Default true.
      ImageGenEnabled: bool
      /// IMAGE_SIZE bot_setting: images/generations `size` param (e.g. "1024x1024"). Default "1024x1024".
      ImageSize: string
      /// IMAGE_QUALITY bot_setting: images/generations `quality` param ("low"|"medium"|"high").
      /// Default "medium".
      ImageQuality: string
      /// LLM_MODELS bot_setting (JSON_BLOB): the model catalog /model shows and switches
      /// between — an array of {"model": "<real model name>", "deployment": "<Azure AI
      /// Foundry deployment id>"}, e.g. [{"model":"gpt-5-mini","deployment":"alita-gpt-5-mini"}].
      /// `deployment` ids carry this bot's namespacing convention for the shared Foundry
      /// account (see dev-bot-settings.sql) and are wire-call-only — /model never shows or
      /// accepts them, only the `model` name, verbatim, no string transformation. Default
      /// "[]" (nothing switchable — replaces the old MODEL_ALLOWLIST, a bare array of
      /// deployment ids that made /model display those ids directly).
      LlmModelsJson: string
      /// SUMMARY_PROMPT bot_setting: system prompt for the /summary command. Default "".
      SummaryPrompt: string
      /// FEATURE_FLAG EMBED_MESSAGES — embed every logged message (user + bot) into
      /// message_embedding for /ask's semantic search. Default true.
      EmbedMessagesEnabled: bool
      /// EMBEDDING_MIN_CHARS bot_setting: messages shorter than this are never embedded
      /// (too little semantic content to be worth indexing). Default 3.
      EmbeddingMinChars: int
      /// ASK_TOP_K bot_setting: how many nearest message_embedding rows /ask pulls as
      /// candidate context before the similarity floor is applied. Default 8.
      AskTopK: int
      /// ASK_SIM_FLOOR bot_setting: minimum cosine similarity (1 - cosine distance) for a
      /// candidate to be included in /ask's context. Default 0.5.
      AskSimFloor: float
      /// ASK_PROMPT bot_setting: system prompt for the /ask command. Default "".
      AskPrompt: string
      /// FEATURE_FLAG DOSSIER_ENABLED — recall injection (dossier summary + matching
      /// facts appended to the LLM system prompt) in ResponderService. Default true.
      /// The nightly extraction job itself always runs regardless of this flag — it only
      /// gates whether the *recall* side reads back what's been learned.
      DossierEnabled: bool
      /// DOSSIER_RECALL_K bot_setting: how many nearest active interaction_memory facts
      /// to recall for a triggering message's author. Default 5.
      DossierRecallK: int
      /// DOSSIER_SIM_FLOOR bot_setting: minimum cosine similarity for a recalled fact.
      /// Default 0.60.
      DossierSimFloor: float
      /// EXTRACT_PROMPT bot_setting: system prompt for the nightly fact-extraction LLM
      /// call (DossierService) — must instruct the model to answer with a JSON array of
      /// short fact strings. Default "".
      ExtractPrompt: string
      /// MERGE_PROMPT bot_setting: system prompt for the nightly summary-merge LLM call
      /// (DossierService) — must instruct the model to answer in RU, max 250 words.
      /// Default "".
      MergePrompt: string
      /// FEATURE_FLAG REWRITER_ENABLED — a second, cheap non-stream LLM call rewrites
      /// ResponderService's final reply text ("перепиши как живой человек в чате...")
      /// before rendering. Default false. Forces the main LLM call to non-stream too (see
      /// ResponderService.Respond) — streaming stays exactly as before when this is off.
      RewriterEnabled: bool
      /// REWRITER_PROMPT bot_setting: system prompt for the rewrite pass. Default "".
      RewriterPrompt: string
      /// OUTCOME_WEIGHTS bot_setting (JSON_BLOB): weighted outcome roll for a TRIGGERED
      /// non-command message — {"reply":100,"silence":0,"emoji":0}. Default keeps the
      /// pre-S6 behavior (always reply). See Services/OutcomeRouter.fs.
      OutcomeWeightsJson: string
      /// ROAST_PROMPT bot_setting: system prompt for the /roast command — must instruct
      /// the model to roast the target using their dossier/facts/quotes, no disclaimers.
      /// Default "".
      RoastPrompt: string
      /// ROAST_COOLDOWN_SECONDS bot_setting: minimum seconds between two /roast calls
      /// against the same target. Default 300.
      RoastCooldownSeconds: int
      /// AWARDS_PROMPT bot_setting: system prompt for the /awards command — must
      /// instruct the model to answer with a strict JSON array of
      /// {title,user,evidence_quote} objects. Default "".
      AwardsPrompt: string
      /// QUOTE_PROMPT bot_setting: system prompt for the /quote command — must instruct
      /// the model to answer with a strict JSON object {author,quote,comment}.
      /// Default "".
      QuotePrompt: string
      /// FEATURE_FLAG DIGEST_ENABLED (Slice 8) — the daily morning-digest scheduled job
      /// (`digest_daily`). Default false — the job runs (lease-acquired, stamped
      /// complete) but sends nothing until this is flipped on.
      DigestEnabled: bool
      /// DIGEST_UTC_HOUR bot_setting: UTC hour the digest job is scheduled to run at
      /// daily (same lease-acquire pattern as `dossier_nightly_update`). Default 7.
      DigestUtcHour: int
      /// DIGEST_MIN_MESSAGES bot_setting: minimum human, non-command `message_log` rows
      /// in the last 24h for a target chat to get a digest (`DbService.HumanMessagesSince`
      /// — same query `/awards`/`/quote` use). Default 30.
      DigestMinMessages: int
      /// DIGEST_PROMPT bot_setting: system prompt for the morning-digest LLM call.
      /// Default "".
      DigestPrompt: string
      /// INTERJECT_PROBABILITY bot_setting: roll in [0,1) gating a willingness-gated
      /// interjection on a non-triggered, non-command text message in a target chat —
      /// checked first (cheapest), before the burst/cooldown DB queries. Default 0.0 (off).
      InterjectProbability: float
      /// BURST_MSGS bot_setting: minimum non-bot `message_log` rows in the burst window
      /// for an interjection to be eligible (`DbService.BurstStats`). Default 8.
      BurstMsgs: int
      /// BURST_SPEAKERS bot_setting: minimum distinct authors in the burst window.
      /// Default 3.
      BurstSpeakers: int
      /// BURST_WINDOW_MINUTES bot_setting: lookback window (minutes) for the burst check.
      /// Default 5.
      BurstWindowMinutes: int
      /// INTERJECT_COOLDOWN_MINUTES bot_setting: an interjection never fires while the
      /// bot has sent ANY message (reply or a previous interjection — both are logged as
      /// `is_bot=TRUE` message_log rows) in this chat within this many minutes
      /// (`DbService.HasBotMessageSince`). Default 30.
      InterjectCooldownMinutes: int
      /// INTERJECT_PROMPT bot_setting: system prompt for the interjection LLM call —
      /// must instruct the model to answer with exactly "PASS" (trimmed, case-
      /// insensitive) when it has nothing worth adding. Default "".
      InterjectPrompt: string
      /// MEME_REACT_PROBABILITY bot_setting: roll in [0,1) gating a meme reaction on a
      /// non-triggered photo message in a target chat. Default 0.0 (off).
      MemeReactProbability: float
      /// MEME_REACT_PROMPT bot_setting: system prompt for the meme-reaction vision LLM
      /// call — must instruct the model to answer with strict JSON
      /// {"action":"react|comment|pass","emoji":"...","text":"..."}; "react" picks ONE
      /// emoji from the same Telegram-allowed reaction set the S6 outcome router uses.
      /// Default "".
      MemeReactPrompt: string
      /// TTS_DEFAULT_VOICE bot_setting: default `/say` voice when no explicit voice arg
      /// is given. Default "alloy" — matches ISpeech.Synthesize's own `voice=None` default.
      TtsDefaultVoice: string
      /// SAY_MAX_CHARS bot_setting: `/say` refuses (RU) text longer than this instead of
      /// synthesizing it. Default 500.
      SayMaxChars: int
      /// ADMIN_USER_IDS bot_setting (JSON_BLOB): user ids allowed to run `/sql`. Default
      /// "[]" — nobody is admin until this is hand-seeded (see AGENTS.md's "Settings
      /// seeds, not migrations").
      AdminUserIdsJson: string
      /// SQL_PROMPT bot_setting: system prompt for `/sql` — must instruct the model to
      /// answer with a strict JSON object {"sql": "..."} containing a single read-only
      /// SELECT/WITH statement against Alita's own schema. Default "".
      SqlPrompt: string
      /// FEATURE_FLAG COST_FOOTER_ENABLED — appends a "⛽ $0.0021" cost line to LLM
      /// responder replies (not command replies), visible in Telegram but stripped before
      /// the message_log insert (so the model never sees its own cost in later context).
      /// Default false.
      CostFooterEnabled: bool
      /// GEMINI_API_KEY env var (secret, like AzureFoundryKey — never a bot_setting).
      /// Empty when unconfigured — GeminiImageGen/GeminiMusicGen then fail gracefully
      /// (RU apology, no doomed network call) instead of crashing.
      GeminiApiKey: string
      /// GEMINI_BASE_URL bot_setting: the Generative Language API base URL. Default the
      /// real Google endpoint; overridden in the fake-suite test fixture to point at
      /// FakeAzureOcrApi's additive `/gemini/*` routes (see AlitaBot.Tests'
      /// ContainerTestBase.fs) — same "swap the base URL, keep the wire format" idiom
      /// AZURE_FOUNDRY_ENDPOINT already uses for the Azure provider.
      GeminiBaseUrl: string
      /// GEMINI_IMAGE_MODEL bot_setting: the Nano Banana model generateContent is called
      /// against for `/img` when IMAGE_PROVIDER=gemini. Default "gemini-3.1-flash-image"
      /// ("Nano Banana 2") — see GeminiProvider.fs's doc comment for the full discovered
      /// model roster and why this tier was picked.
      GeminiImageModel: string
      /// GEMINI_MUSIC_MODEL bot_setting: the Lyria model `/song` calls. Default
      /// "lyria-3-pro-preview" — see GeminiProvider.fs's doc comment.
      GeminiMusicModel: string
      /// IMAGE_PROVIDER bot_setting: "azure" | "gemini" — which IImageGen `/img` routes
      /// to (see Llm/GeminiProvider.fs's ImageGenRouter). Default "gemini" — Azure image
      /// quota is still 0 in this subscription (AlitaBot/docs/TECH-DEBT.md); Gemini is the
      /// only real image-gen path until that quota lands, at which point flipping this
      /// setting live switches `/img` back to Azure with zero code changes.
      ImageProvider: string
      /// SONG_MAX_CHARS bot_setting: `/song` refuses (RU) a style+lyrics prompt longer
      /// than this instead of generating it. Default 1000.
      SongMaxChars: int
      /// SONG_COOLDOWN_SECONDS bot_setting: minimum seconds between two `/song` calls from
      /// the same user (music generation is slow and pricier than images/chat). Default 120.
      SongCooldownSeconds: int
      /// FEATURE_FLAG NL_TOOLS_ENABLED (S10 PR1) — natural-language tool-calling loop
      /// (generate_image, web_search) for the LLM responder path. DB-only, no env fallback —
      /// dev seed "true", PROD seeded "false" (see AGENTS.md's "Settings seeds, not
      /// migrations"; flip live via /reload-settings after staging validation).
      NlToolsEnabled: bool
      /// NL_TOOLS_MAX_ITERATIONS bot_setting: hard cap on tool-call rounds per turn — once
      /// exceeded, AgentToolLoop strips Tools from the next request, forcing a clean final
      /// text answer instead of looping forever. Default 4.
      NlToolsMaxIterations: int
      /// NL_TOOLS_RATE_LIMIT_PER_HOUR bot_setting: per-user hourly cap (via llm_usage) on
      /// cost-heavy tool calls (generate_image, generate_song [PR2], web_search). Default 20.
      NlToolsRateLimitPerHour: int
      /// TOOL_USE_PROMPT bot_setting: appended to the system prompt whenever tools are
      /// offered to the model — must instruct it to use tools immediately when explicitly
      /// asked, with no pre-announcement, react to results in its own style, never
      /// repeat the request/prompt verbatim, and (S10 staging finding, Bug 2) default to an
      /// EMPTY final reply once a media tool already delivered its result with a caption —
      /// AgentToolLoop's duplicate-final-reply guard is the deterministic backstop for this
      /// same rule, not a substitute for it. Default "".
      ToolUsePrompt: string
      /// MEDIA_CAPTION_PROMPT bot_setting: system-prompt addition for
      /// MediaActions.composeCaption — one short in-character phrase reacting to having
      /// just generated media, never describing the result or repeating the request.
      /// Default "".
      MediaCaptionPrompt: string
      /// FEATURE_FLAG WEB_SEARCH_ENABLED — per-tool kill switch for the web_search NL tool,
      /// independent of NL_TOOLS_ENABLED (which gates the whole loop). Default true.
      WebSearchEnabled: bool
      /// AZURE_RESPONSES_ENDPOINT bot_setting: base URL for the Azure Responses API
      /// (web_search tool) — a DIFFERENT host than AzureFoundryEndpoint on the same Azure AI
      /// Foundry resource, see Llm/AzureResponsesProvider.fs's DISCOVERY doc comment.
      AzureResponsesEndpoint: string
      /// WEB_SEARCH_MODEL bot_setting: the `model` value the Responses API call sends.
      /// Empty means unconfigured — web_search then fails gracefully (RU apology) instead of
      /// a doomed network call, same posture as an empty ImageDeployment.
      WebSearchModel: string
      TestMode: bool }
