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
      TestMode: bool }
