-- Settings storage for non-secret bot configuration.
-- Secrets (BOT_TELEGRAM_TOKEN, BOT_AUTH_TOKEN, DATABASE_URL, AZURE_OCR_KEY, GITHUB_TOKEN)
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
GRANT SELECT, INSERT, UPDATE ON bot_setting TO coupon_hub_bot_service;
