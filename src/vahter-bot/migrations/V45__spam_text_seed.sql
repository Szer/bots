-- Postgres-backed replacement for the in-process ban-seeded spam-text cache (SpamTextCache.fs).
-- A single-pod ConcurrentDictionary can't see a /ban handled by a different pod, halving the
-- exact-repeat catch rate with 2+ replicas. Normalized text is the primary key (same conservative
-- normalization as before — see SpamTextCache.fs); TTL is enforced at read time via expires_at,
-- expired rows are swept by the existing daily cleanup job (Cleanup.fs).
CREATE TABLE spam_text_seed (
    normalized_text TEXT        PRIMARY KEY,
    chat_id         BIGINT      NOT NULL,
    message_id      BIGINT      NOT NULL,
    seeded_at       TIMESTAMPTZ NOT NULL,
    expires_at      TIMESTAMPTZ NOT NULL
);

-- Cleanup job sweeps by expires_at; TryGet also filters expires_at > now() as a belt-and-braces check.
CREATE INDEX idx_spam_text_seed_expires_at ON spam_text_seed (expires_at);

GRANT SELECT, INSERT, UPDATE, DELETE ON spam_text_seed TO vahter_bot_ban_service;
