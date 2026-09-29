CREATE TABLE moderation_quality_daily (
    day date NOT NULL,
    cohort text NOT NULL CHECK (cohort IN ('all_scored', 'llm_triaged')),
    system text NOT NULL CHECK (system IN ('pipeline', 'ml', 'llm')),
    llm_model text,
    tp bigint NOT NULL DEFAULT 0,
    tn bigint NOT NULL DEFAULT 0,
    fp bigint NOT NULL DEFAULT 0,
    fn bigint NOT NULL DEFAULT 0,
    abstained bigint NOT NULL DEFAULT 0,
    unresolved bigint NOT NULL DEFAULT 0,
    excluded bigint NOT NULL DEFAULT 0,
    computed_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE NULLS NOT DISTINCT (day, cohort, system, llm_model)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON moderation_quality_daily TO vahter_bot_ban_service;
INSERT INTO scheduled_job (job_name) VALUES ('moderation_quality_daily');
INSERT INTO bot_setting (key, value, type, feature_group, description) VALUES
    ('QUALITY_SETTLING_DAYS', '14', 'FREE_FORM', 'quality', 'Full UTC days after day end before quality aggregation'),
    ('QUALITY_BACKFILL_DAYS', '7', 'FREE_FORM', 'quality', 'Maximum missing historical days processed per daily run');
