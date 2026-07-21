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
      TestMode: bool }
