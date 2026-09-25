-- Aggregate snapshots for BotInfra.EventStore (see src/BotInfra/EVENTSTORE.md). A disposable
-- cache: truncating it is always safe, loads fall back to a full stream replay.
CREATE TABLE IF NOT EXISTS event_snapshot (
    stream_id      TEXT        NOT NULL,
    state_type     TEXT        NOT NULL,
    schema_version INT         NOT NULL,
    stream_version INT         NOT NULL,
    state          JSONB       NOT NULL,
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (stream_id, state_type)
);

GRANT SELECT, INSERT, UPDATE, DELETE ON event_snapshot TO vahter_bot_ban_service;
