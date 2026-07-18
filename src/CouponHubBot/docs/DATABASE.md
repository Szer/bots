# Database

## Engine

PostgreSQL 17.10. Migrations managed by Flyway.

## Schema Notes

### coupon table

Key columns:
- `status TEXT NOT NULL DEFAULT 'available'` — valid values: `available`, `taken`, `used`, `voided`
- `taken_by BIGINT NULL` — user who took the coupon (NULL when available/voided)
- `barcode_text TEXT NULL` — barcode decoded from coupon photo

### pending_add table

Wizard state for `/add` flow:

### coupon_event table

Event types: `added`, `taken`, `returned`, `used`, `voided`

### chat_message table

Stores messages from the community group chat for product analysis. Only the community chat (`COMMUNITY_CHAT_ID`) is monitored. Bot messages are excluded.

Key columns:
- `chat_id BIGINT` + `message_id INT` — unique per message
- `user_id BIGINT` — sender identifier: Telegram user ID for regular messages, or `SenderChat.Id` for anonymous admins / channel posts
- `text TEXT NULL` — message text (NULL for media-only messages)
- `has_photo`, `has_document` — media flags (content is not stored)
- `reply_to_message_id INT NULL` — enables conversation threading analysis
- `created_at TIMESTAMPTZ` — when the message was saved

Retention: 1 year. Cleanup runs daily via `ReminderService`.

### user_feedback table

Stores user feedback submitted via `/feedback` command. Each row links back to the user and optionally to a GitHub issue.

Key columns:
- `user_id BIGINT` — references `user(id)`, the user who submitted feedback
- `feedback_text TEXT NULL` — text content (NULL for media-only messages)
- `has_media BOOLEAN` — whether the feedback message contained media (photo, document, voice, video)
- `telegram_message_id INT NULL` — original Telegram message ID
- `github_issue_number INT NULL` — linked GitHub issue number (NULL if GitHub integration is disabled or failed)
- `created_at TIMESTAMPTZ` — when the feedback was submitted

## Migrations

Migration files live in `src/migrations/` (V1 through V11+). Flyway runs them:
- In tests: via Testcontainers Flyway container
- In CI/CD: via Docker against production DB over WireGuard VPN

## Access Control

Application connects as role `coupon_hub_bot_service`. When adding a **new table** or changing access, grant it to this role — in the same migration, or in a dedicated later grants migration (e.g. `V3__missing_grants.sql`, which also grants `ALL SEQUENCES IN SCHEMA public`). The Testcontainers suite runs every migration and exercises the queries, so a genuinely missing grant fails CI.

Example:
```sql
GRANT SELECT, INSERT, UPDATE, DELETE ON new_table TO coupon_hub_bot_service;
GRANT USAGE, SELECT ON SEQUENCE new_table_id_seq TO coupon_hub_bot_service;
```

## Init Script

`init.sql` at repo root creates the database, roles, and grants initial permissions. It runs once during test container setup via `dbContainer.ExecScriptAsync(initSql)`.
