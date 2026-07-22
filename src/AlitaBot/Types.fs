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
      /// MODEL_ALLOWLIST bot_setting (JSON_BLOB): array of LLM_DEPLOYMENT values /model may
      /// switch to, e.g. ["alita-gpt-5-mini"]. Default "[]" (nothing switchable).
      ModelAllowlistJson: string
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
      TestMode: bool }
