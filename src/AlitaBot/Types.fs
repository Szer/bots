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
      /// LLM_PRICING bot_setting: {"gpt-5-mini":{"input_per_1m":0.25,"output_per_1m":2.00}}
      LlmPricingJson: string
      /// "echo" | "llm"
      ResponderMode: string
      /// "draft" | "edit" | "plain"
      StreamMode: string
      ContextWindowMessages: int
      TestMode: bool }
