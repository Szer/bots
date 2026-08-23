namespace CouponHubBot

open System
open System.Text.Json.Serialization
open CouponHubBot

[<CLIMutable>]
type BotConfiguration =
    { BotToken: string
      SecretToken: string
      CommunityChatId: int64
      TelegramApiBaseUrl: string | null
      ReminderHourDublin: int
      ReminderRunOnStart: bool
      OcrEnabled: bool
      OcrMaxFileSizeBytes: int64
      AzureOcrEndpoint: string
      AzureOcrKey: string
      FeedbackAdminIds: int64 array
      GitHubToken: string
      GitHubRepo: string
      /// bot_setting key (env fallback WEBHOOK_URL) — full webhook URL to self-register
      /// at startup (WebhookRegistrationService.fs). Empty/absent means "do nothing":
      /// production's webhook is set once, manually (README.dev.md), and must keep
      /// working untouched with no config present.
      WebhookUrl: string
      TestMode: bool
      MaxTakenCoupons: int
      BatchDebounceMs: int }

[<CLIMutable>]
type DbUser =
    { id: int64
      username: string | null
      first_name: string | null
      last_name: string | null
      created_at: DateTime
      updated_at: DateTime }

[<CLIMutable>]
type Coupon =
    { id: int
      owner_id: int64
      photo_file_id: string
      value: decimal
      min_check: decimal
      expires_at: DateOnly
      barcode_text: string | null
      status: string
      taken_by: Nullable<int64>
      taken_at: Nullable<DateTime>
      created_at: DateTime
      valid_from: Nullable<DateOnly> }

/// OCR result for coupon photo.
/// Each field is optional: when present it's trusted enough to pre-fill /add wizard,
/// and the user still confirms everything before saving.
[<CLIMutable>]
type CouponOCR =
    { couponValue: Nullable<decimal>
      minCheck: Nullable<decimal>
      validFrom: Nullable<DateTime>
      validTo: Nullable<DateTime>
      barcode: string | null
      /// True when the OCR *backend* call failed (network/timeout, after the resilience pipeline's
      /// retries) rather than succeeding with no usable text. Lets callers report "OCR failed"
      /// (a transient outage — retry) distinctly from "no barcode" (a readable but unusable photo).
      backendFailed: bool }

[<CLIMutable>]
type CouponEvent =
    { id: int
      coupon_id: int
      user_id: int64
      event_type: string
      created_at: DateTime }

[<CLIMutable>]
type CouponEventHistoryRow =
    { date: string
      user: string
      event_type: string }

/// Per-user all-time give/take totals for /whois. Both counts/sums net out
/// "<type>_reverted" the same way GetUserStats does.
[<CLIMutable>]
type UserContributionStats =
    { added_count: int64
      added_value: decimal
      taken_count: int64
      taken_value: decimal }

/// One coupon_event row for /whois's "last 10 actions". value/min_check are nullable
/// because the join to coupon is a LEFT JOIN (defensive: coupon_id's FK is ON DELETE
/// CASCADE, so nothing in the app can orphan an event today, but the row shape survives it).
[<CLIMutable>]
type UserActionRow =
    { date: string
      event_type: string
      coupon_id: int
      value: Nullable<decimal>
      min_check: Nullable<decimal> }

[<CLIMutable>]
type UserFeedbackRow =
    { id: int64
      user_id: int64
      feedback_text: string | null
      has_media: bool
      telegram_message_id: Nullable<int64>
      github_issue_number: Nullable<int>
      created_at: DateTime }

/// Used by FakeTgApi test endpoints (serialize minimal info)
[<CLIMutable>]
type ApiCallLog =
    { Method: string
      RequestBody: string
      Timestamp: DateTime
      CorrelationId: string | null }

