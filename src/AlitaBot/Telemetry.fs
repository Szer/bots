namespace AlitaBot

open System.Diagnostics
open System.Diagnostics.Metrics
open Funogram.Telegram.Types
open BotInfra

/// ActivitySource for custom spans in traces (OTEL). Used by AddOpenTelemetry in Program.
module Telemetry =
    let botActivity = new ActivitySource("AlitaBot")

    /// Best-effort identity tags common to all Update variants, meant for the top
    /// `postUpdate` span — mirrors VahterBanBot.Telemetry.updateIdentityTags so a
    /// single-spanset Tempo query works the same way across bots. fromUserId/fromUsername
    /// = the human who caused the update (message author, button-pressing user). Tag
    /// names match VahterBanBot's, not AlitaBot's own inner-span tags ("fromId") — kept
    /// separate so cross-bot queries stay uniform. Absent values are skipped.
    let updateIdentityTags (update: Update) : (string * obj) list =
        let ofUser (user: User option) =
            [ match user with
              | Some u ->
                  yield "fromUserId", box u.Id
                  match u.Username with
                  | Some username -> yield "fromUsername", box username
                  | None -> ()
              | None -> () ]
        let ofChat (chat: Chat) =
            [ yield "chatId", box chat.Id
              match chat.Username with
              | Some username -> yield "chatUsername", box username
              | None -> () ]
        match update.CallbackQuery, update.MessageReaction, update.EditedOrMessage with
        | Some cq, _, _ ->
            [ yield "updateType", box "callback_query"
              yield! ofUser (Some cq.From)
              match cq.Message with
              | Some (MaybeInaccessibleMessage.Message m) ->
                  yield! ofChat m.Chat
                  yield "messageId", box m.MessageId
              | Some (MaybeInaccessibleMessage.InaccessibleMessage m) ->
                  yield! ofChat m.Chat
                  yield "messageId", box m.MessageId
              | None -> () ]
        | None, Some reaction, _ ->
            [ yield "updateType", box "message_reaction"
              // User is None for anonymous (ActorChat) reactions
              yield! ofUser reaction.User
              yield! ofChat reaction.Chat
              yield "messageId", box reaction.MessageId ]
        | None, None, Some message ->
            [ yield "updateType", box (if update.EditedMessage.IsSome then "edited_message" else "message")
              // channel-sender posts have From = None
              yield! ofUser message.From
              yield! ofChat message.Chat
              yield "messageId", box message.MessageId ]
        | None, None, None ->
            [ if update.ChatMember.IsSome then yield "updateType", box "chat_member"
              elif update.MyChatMember.IsSome then yield "updateType", box "my_chat_member"
              else yield "updateType", box "unknown" ]

    /// Applies updateIdentityTags to an activity. Null-safe: StartActivity returns
    /// null when no listener samples the source.
    let setUpdateIdentityTags (activity: Activity) (update: Update) =
        if not (isNull activity) then
            for name, value in updateIdentityTags update do
                %activity.SetTag(name, value)

module Metrics =
    let meter = new Meter("AlitaBot.Metrics")

    /// Count of processed messages, tagged by `outcome` ∈ {logged, replied, ignored,
    /// duplicate_update, voice_*, image_*, ...} — see BotService for the full set.
    let messagesTotal = meter.CreateCounter<int64>("alitabot_messages_total")

    /// Count of explicit command invocations, tagged by `command` (e.g. "img") —
    /// mirrors CouponHubBot.Metrics.commandTotal. Triggered mentions/replies are NOT
    /// commands and stay out of this counter (they're covered by messagesTotal's
    /// `replied` outcome).
    let commandTotal = meter.CreateCounter<int64>("alitabot_command_total")

    /// Count of voice/video-note/audio transcription attempts, tagged by `outcome`
    /// (e.g. "transcribed", "empty_transcript", "failed", "disabled", "no_filepath").
    let voiceTranscribeTotal = meter.CreateCounter<int64>("alitabot_voice_transcribe_total")

    /// Wall-clock duration of the voice pipeline (Telegram file download + STT call),
    /// milliseconds. Only recorded when transcription was actually attempted (i.e. not
    /// for VOICE_TRANSCRIBE_ENABLED=false).
    let voiceTranscribeDurationMs = meter.CreateHistogram<float>("alitabot_voice_transcribe_duration_ms")

    /// Count of embedding-pipeline failures (Slice 5a) — an LLM error from IEmbeddings
    /// or an exception inserting the message_embedding row. Always Warning-logged
    /// alongside this counter, never surfaced to the reply path — see BotService's
    /// `tryEmbed`.
    let embeddingFailuresTotal = meter.CreateCounter<int64>("alitabot_embedding_failures_total")

    /// Count of MarkdownV2-formatted final-message deliveries Telegram rejected (400 bad
    /// entities) — the renderer falls back to a plain-text resend/edit every time this
    /// fires, so a sustained nonzero rate means `MarkdownRenderer.toMarkdownV2` is
    /// producing entities Telegram doesn't accept (see Services/ReplyRenderer.fs's
    /// `Mdv2Delivery`).
    let mdv2FallbackTotal = meter.CreateCounter<int64>("alitabot_mdv2_fallback_total")
