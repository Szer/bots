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
      TestMode: bool }
