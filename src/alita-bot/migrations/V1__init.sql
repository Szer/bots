-- Settings storage for non-secret bot configuration (same shape as coupon-hub-bot).
-- Secrets (BOT_TELEGRAM_TOKEN, BOT_AUTH_TOKEN, DATABASE_URL, AZURE_FOUNDRY_KEY)
-- remain in environment variables / Azure Key Vault.
CREATE TABLE bot_setting (
    key           TEXT        PRIMARY KEY,
    value         TEXT,                     -- NULL means use the hardcoded default
    type          TEXT        NOT NULL
                  CHECK (type IN ('FEATURE_FLAG', 'JSON_BLOB', 'FREE_FORM')),
    feature_group TEXT        NOT NULL,
    description   TEXT,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX ix_bot_setting_feature_group ON bot_setting (feature_group);

-- Service account needs read + write; DELETE is intentionally withheld
-- (settings are deactivated by value, not removed)
GRANT SELECT, INSERT, UPDATE ON bot_setting TO alita_bot_service;

-- Full conversational log: user messages AND the bot's own replies (is_bot=true).
-- UNIQUE(chat_id, message_id) makes webhook redelivery idempotent.
CREATE TABLE message_log (
    id                  BIGSERIAL   PRIMARY KEY,
    chat_id             BIGINT      NOT NULL,
    message_id          BIGINT      NOT NULL,
    user_id             BIGINT      NOT NULL,
    username            TEXT,
    display_name        TEXT        NOT NULL,
    is_bot              BOOLEAN     NOT NULL DEFAULT FALSE,
    reply_to_message_id BIGINT,
    text                TEXT        NOT NULL,
    sent_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (chat_id, message_id)
);

CREATE INDEX ix_message_log_chat_sent_at ON message_log (chat_id, sent_at DESC);

GRANT SELECT, INSERT ON message_log TO alita_bot_service;
GRANT USAGE, SELECT ON SEQUENCE message_log_id_seq TO alita_bot_service;
