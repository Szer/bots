-- Album upload batch flow: one row per in-flight album, one child row per photo.
-- Process-as-arrives: items get OCR'd in background as their webhook arrives;
-- the close-handler fires 5s after the last photo and renders one bulk-confirm.
CREATE TABLE pending_add_batch (
    id              BIGSERIAL PRIMARY KEY,
    user_id         BIGINT      NOT NULL REFERENCES "user"(id) ON DELETE CASCADE,
    media_group_id  TEXT        NOT NULL,
    bulk_chat_id    BIGINT      NOT NULL,
    bulk_message_id INTEGER     NULL,
    status          TEXT        NOT NULL DEFAULT 'open',
    created_at      TIMESTAMPTZ NOT NULL DEFAULT timezone('utc', NOW()),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT timezone('utc', NOW())
);

-- At most one active batch per (user, media_group_id). Partial index so we can
-- use it as an ON CONFLICT target without blocking historical/terminated batches.
CREATE UNIQUE INDEX pending_add_batch_user_mgid_active_uniq
    ON pending_add_batch (user_id, media_group_id)
    WHERE status IN ('open', 'awaiting_user');

CREATE INDEX pending_add_batch_open_idx
    ON pending_add_batch (updated_at)
    WHERE status = 'open';

CREATE TABLE pending_add_batch_item (
    id                BIGSERIAL PRIMARY KEY,
    batch_id          BIGINT      NOT NULL REFERENCES pending_add_batch(id) ON DELETE CASCADE,
    seq               INTEGER     NOT NULL,
    photo_file_id     TEXT        NOT NULL,
    photo_message_id  INTEGER     NOT NULL,
    -- Lifecycle: 'pending' (OCR in flight) -> 'ok' | 'needs_input';
    -- after confirm: 'inserted' | 'failed'.
    status            TEXT        NOT NULL,
    value             NUMERIC(10,2) NULL,
    min_check         NUMERIC(10,2) NULL,
    expires_at        DATE          NULL,
    barcode_text      TEXT          NULL,
    valid_from        DATE          NULL,
    failure_note      TEXT          NULL,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT timezone('utc', NOW()),
    UNIQUE (batch_id, seq),
    UNIQUE (batch_id, photo_file_id)
);

CREATE INDEX pending_add_batch_item_pending_idx
    ON pending_add_batch_item (batch_id)
    WHERE status = 'pending';

GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.pending_add_batch      TO coupon_hub_bot_service;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.pending_add_batch_item TO coupon_hub_bot_service;
GRANT USAGE, SELECT ON SEQUENCE public.pending_add_batch_id_seq      TO coupon_hub_bot_service;
GRANT USAGE, SELECT ON SEQUENCE public.pending_add_batch_item_id_seq TO coupon_hub_bot_service;

-- Seed the debounce window so a fresh DB has a value (no env fallback in buildBotConf).
INSERT INTO bot_setting (key, value, type, feature_group, description) VALUES
    ('BATCH_DEBOUNCE_MS', '5000', 'FREE_FORM', 'BATCH',
     'How long to wait for more album photos before processing the batch (ms)')
ON CONFLICT (key) DO NOTHING;
