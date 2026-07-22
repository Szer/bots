-- Gemini provider slice: Nano Banana images (alternative /img backend, IMAGE_PROVIDER-
-- routed — no schema change needed, it reuses llm_usage.kind='image') + Lyria music
-- (/song). Adds 'music' to llm_usage.kind's CHECK, and a per-user cooldown table mirroring
-- V5's roast_cooldown (but per-INVOKER, not per-target — /song has no target concept).

ALTER TABLE llm_usage DROP CONSTRAINT llm_usage_kind_check;
ALTER TABLE llm_usage ADD CONSTRAINT llm_usage_kind_check
    CHECK (kind IN ('chat', 'stt', 'tts', 'image', 'embedding', 'music'));

-- Per-user cooldown for /song (SONG_COOLDOWN_SECONDS bot_setting, default 120s) — one row
-- per person who has ever generated a song, upserted (not appended) since only the most
-- recent generation time matters for the cooldown check.
CREATE TABLE song_cooldown (
    user_id      BIGINT      PRIMARY KEY,
    last_song_at TIMESTAMPTZ NOT NULL
);

GRANT SELECT, INSERT, UPDATE ON song_cooldown TO alita_bot_service;
