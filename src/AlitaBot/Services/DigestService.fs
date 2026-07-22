namespace AlitaBot.Services

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open AlitaBot
open AlitaBot.Llm
open AlitaBot.Telemetry
open BotInfra

/// Slice 8: the daily morning digest — one message per target chat, summarizing the
/// previous 24h's human conversation with a light sarcastic tone (`DIGEST_PROMPT`).
/// Driven by `SchedulerHostedService` (`ScheduledJobs.fs`), same lease-acquire/
/// `/test/run-job?name=digest_daily` pattern as `DossierService`/`dossier_nightly_update`.
/// `DIGEST_ENABLED` (default false) gates the whole thing here, not at the scheduler
/// level — the lease is still acquired/stamped-complete on schedule either way, it just
/// does nothing while disabled, mirroring the always-off-until-tuned posture every Slice 8
/// feature takes.
type DigestService
    (
        db: DbService,
        chat: IChatCompletion,
        tg: ITelegramApi,
        options: IOptions<BotConfiguration>,
        logger: ILogger<DigestService>,
        time: TimeProvider
    ) =

    /// Cap on how many `HumanMessagesSince` rows feed one chat's digest transcript — a
    /// whole day of a busy chat could otherwise run unbounded; mirrors `/awards`'
    /// `AwardsTranscriptCap` (also a 7-day window, similar order of magnitude per day).
    [<Literal>]
    let DigestTranscriptCap = 2000

    let buildTranscript (rows: MessageLogRow[]) : string =
        rows |> Array.map (fun r -> $"[{r.display_name}]: {r.text}") |> String.concat "\n"

    let digestRequest (conf: BotConfiguration) (transcript: string) : ChatRequest =
        { Deployment = conf.LlmDeployment
          Messages =
            [ { Role = ChatRole.System
                Content = [ ContentPart.Text conf.DigestPrompt ]
                ToolCalls = []
                ToolCallId = None }
              { Role = ChatRole.User
                Content = [ ContentPart.Text transcript ]
                ToolCalls = []
                ToolCallId = None } ]
          Tools = []
          Temperature = None
          MaxTokens = None }

    let logRow (chatId: int64) (messageId: int64) (conf: BotConfiguration) (text: string) : MessageLogRow =
        { chat_id = chatId
          message_id = messageId
          user_id = BotHelpers.botUserId conf
          username = conf.BotUsername
          display_name = conf.BotUsername
          is_bot = true
          reply_to_message_id = Nullable()
          text = text
          sent_at = time.GetUtcNow().UtcDateTime }

    /// One target chat's digest attempt: below `DIGEST_MIN_MESSAGES` -> no-op (span-tagged
    /// only, no LLM call); otherwise a non-stream `DIGEST_PROMPT` call, MDV2-rendered and
    /// sent as a fresh (non-reply) message via `Mdv2Delivery.sendFinalToChat`, then logged
    /// to `message_log` (feeds later context/embedding exactly like any other bot message).
    member _.ProcessChat(chatId: int64, since: DateTime) : Task<unit> =
        task {
            let conf = options.Value
            use span = botActivity.StartActivity("digest.generate")
            %span.SetTag("chatId", chatId)

            let! rows = db.HumanMessagesSince(chatId, since, DigestTranscriptCap)
            %span.SetTag("messageCount", rows.Length)

            if rows.Length < conf.DigestMinMessages then
                %span.SetTag("outcome", "below_min_messages")
            else
                let transcript = buildTranscript rows
                let usageCtx: UsageContext = { ChatId = Some chatId; UserId = None }

                match! chat.Complete(digestRequest conf transcript, usageCtx, CancellationToken.None) with
                | Error err ->
                    %span.SetTag("outcome", "failed")
                    logger.LogWarning("Digest generation failed for chat {ChatId}: {Error}", chatId, string err)
                | Ok resp when String.IsNullOrWhiteSpace resp.Text ->
                    %span.SetTag("outcome", "empty_response")
                    logger.LogWarning("Digest LLM call returned empty text for chat {ChatId}", chatId)
                | Ok resp ->
                    let! sent = Mdv2Delivery.sendFinalToChat tg logger chatId resp.Text
                    let! _ = db.LogMessage(logRow chatId sent.MessageId conf resp.Text)
                    Metrics.proactiveTotal.Add(1L, Collections.Generic.KeyValuePair("kind", box "digest"))
                    %span.SetTag("outcome", "sent")
        }

    /// Every `TargetChatIds` chat, once per run. Per-chat failures are caught and logged
    /// so one chat's failure can't abort the rest — same posture as
    /// `DossierService.RunNightlyUpdate`'s per-user isolation.
    member this.RunDailyDigest() : Task<unit> =
        task {
            let conf = options.Value

            if not conf.DigestEnabled then
                logger.LogInformation("DigestService: DIGEST_ENABLED=false — skipping")
            else
                let since = time.GetUtcNow().UtcDateTime.AddHours(-24.0)
                logger.LogInformation("DigestService: daily digest for {Count} target chat(s)", conf.TargetChatIds.Length)

                for chatId in conf.TargetChatIds do
                    try
                        do! this.ProcessChat(chatId, since)
                    with ex ->
                        logger.LogError(ex, "DigestService: failed to build a digest for chat {ChatId}", chatId)
        }
