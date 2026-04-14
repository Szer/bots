-- Bot settings (key-value pairs loaded at startup, reloadable at runtime)
CREATE TABLE bot_setting (
    key           TEXT        PRIMARY KEY,
    value         TEXT,                     -- NULL means use the hardcoded default
    type          TEXT        NOT NULL
                  CHECK (type IN ('FEATURE_FLAG', 'JSON_BLOB', 'FREE_FORM')),
    feature_group TEXT        NOT NULL,
    description   TEXT,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX ix_bot_setting_feature_group ON bot_setting (feature_group);

-- Service account permissions
GRANT SELECT, INSERT, UPDATE ON bot_setting TO alita_bot_service;

-- Per-person dossier: LLM-generated cumulative summary
CREATE TABLE person_dossier (
    user_id      BIGINT      PRIMARY KEY,
    username     TEXT,
    display_name TEXT,
    summary      TEXT,        -- LLM-generated cumulative paragraph, grows over time
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);

GRANT SELECT, INSERT, UPDATE ON person_dossier TO alita_bot_service;

-- Individual memory facts extracted per person (accumulated, never deleted)
-- Each row is one key fact/observation, embedded for semantic similarity recall.
CREATE TABLE interaction_memory (
    id         BIGSERIAL    PRIMARY KEY,
    user_id    BIGINT       NOT NULL,
    content    TEXT         NOT NULL,    -- plain text fact
    embedding  vector(1536),             -- Azure text-embedding-3-small (1536 dims)
    created_at TIMESTAMPTZ  NOT NULL DEFAULT now()
);

CREATE INDEX ON interaction_memory (user_id, created_at DESC);
CREATE INDEX ON interaction_memory USING ivfflat (embedding vector_cosine_ops) WITH (lists = 50);

GRANT SELECT, INSERT ON interaction_memory TO alita_bot_service;
GRANT USAGE, SELECT ON SEQUENCE interaction_memory_id_seq TO alita_bot_service;

-- Raw message log: input for daily dossier refresh, purged after MessageLogRetentionDays
CREATE TABLE message_log (
    id       BIGSERIAL    PRIMARY KEY,
    user_id  BIGINT       NOT NULL,
    chat_id  BIGINT       NOT NULL,
    message  TEXT         NOT NULL,
    sent_at  TIMESTAMPTZ  NOT NULL
);

CREATE INDEX ON message_log (user_id, sent_at DESC);
CREATE INDEX ON message_log (sent_at);  -- for cleanup job

GRANT SELECT, INSERT ON message_log TO alita_bot_service;
GRANT USAGE, SELECT ON SEQUENCE message_log_id_seq TO alita_bot_service;
GRANT DELETE ON message_log TO alita_bot_service;

-- LLM-summarised news items for proactive posting
CREATE TABLE news_summary (
    id         BIGSERIAL    PRIMARY KEY,
    source_url TEXT         NOT NULL,
    summary    TEXT         NOT NULL,    -- Russian casual-tone LLM summary
    fetched_at TIMESTAMPTZ  NOT NULL DEFAULT now(),
    posted     BOOLEAN      NOT NULL DEFAULT false
);

GRANT SELECT, INSERT, UPDATE ON news_summary TO alita_bot_service;
GRANT USAGE, SELECT ON SEQUENCE news_summary_id_seq TO alita_bot_service;

-- Distributed scheduled-job locking (same pattern as VahterBanBot)
CREATE TABLE scheduled_job (
    job_name          TEXT        PRIMARY KEY,
    last_completed_at TIMESTAMPTZ,
    locked_until      TIMESTAMPTZ,
    locked_by         TEXT
);

INSERT INTO scheduled_job (job_name) VALUES
    ('daily_dossier_update'),
    ('daily_news_fetch'),
    ('daily_cleanup');

GRANT SELECT, UPDATE ON scheduled_job TO alita_bot_service;
