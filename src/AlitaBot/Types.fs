namespace AlitaBot

open System

[<CLIMutable>]
type PersonDossier =
    { UserId:      int64
      Username:    string
      DisplayName: string
      Summary:     string
      UpdatedAt:   DateTime }

[<CLIMutable>]
type InteractionMemory =
    { Id:        int64
      UserId:    int64
      Content:   string
      CreatedAt: DateTime }

[<CLIMutable>]
type NewsItem =
    { Id:        int64
      SourceUrl: string
      Summary:   string
      FetchedAt: DateTime
      Posted:    bool }

[<CLIMutable>]
type MessageLogRow =
    { Id:      int64
      UserId:  int64
      ChatId:  int64
      Message: string
      SentAt:  DateTime }

/// All runtime configuration for AlitaBot.
/// Required env vars are read at startup; settings with sane defaults fall back to bot_setting table.
type BotConfiguration =
    { BotToken:       string
      SecretToken:    string
      TargetChatId:   int64
      BotUsername:    string        // e.g. "Alita" — used to detect mentions
      TelegramApiBaseUrl: string    // null = production Telegram

      // Azure AI Foundry (all three layers share the same endpoint + key)
      AzureFoundryEndpoint:       string
      AzureFoundryKey:            string
      ResponderDeployment:        string   // default "gpt-4o"
      RewriterDeployment:         string   // default "gpt-4o"
      CensorDeployment:           string   // default "gpt-4o"  (EmojiMeme + Chaos paths)
      EmbeddingDeployment:        string   // default "text-embedding-3-small"

      // Layer 3 routing weights (normalised at runtime; do not need to sum to 100)
      Layer3WeightSilence:   int    // default 30
      Layer3WeightUsual:     int    // default 45
      Layer3WeightEmojiMeme: int    // default 20
      Layer3WeightChaos:     int    // default 5

      // News fetching
      NewsSourceUrls:        string[]  // curated page URLs loaded from bot_setting JSON array
      NewsFetchIntervalHours: int      // default 4

      // Proactive posting
      ProactiveActiveHoursStart: int   // UTC hour, default 9
      ProactiveActiveHoursEnd:   int   // UTC hour, default 23
      ProactivePostProbability:  float // default 0.2

      // Dossier update
      DossierUpdateHourUtc: int        // default 2 (02:00 UTC)

      // Cleanup
      MessageLogRetentionDays: int     // default 365

      // Context windows
      ConversationContextMessages: int // default 20
      TopMemoriesPerUser:          int // default 5

      TestMode: bool }
