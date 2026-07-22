-- Slice 5b: per-person dossiers with nightly fact extraction.
--
-- Shapes adapted from the old (pre-Funogram) `feature/alita-bot` branch's
-- `Services/DossierService.fs` / `migrations/V1__init.sql` (see docs/TECH-DEBT.md's
-- now-resolved "ScheduledJobs.fs cherry-pick deferred" entry), with three fixes over
-- that design:
--   1. `interaction_memory` uses HNSW (not ivfflat, which needs pre-existing rows to
--      pick sane list/probe counts and degrades to a full scan on an empty/small table).
--   2. `interaction_memory` gets `valid_from`/`valid_to` (+ a partial "active" index) so
--      a fact can be superseded later without deleting history — nothing sets `valid_to`
--      yet (dedup below just skips a near-duplicate fact rather than superseding), but the
--      column and index are in place for that follow-up.
--   3. Recall (ResponderService) and nightly dedup (DossierService) both apply an explicit
--      cosine-similarity floor — never "just take the nearest N regardless of relevance".

-- Distributed scheduled-job locking — same UPDATE...RETURNING lease pattern as the old
-- BotInfra.ScheduledJobs module (see Services/ScheduledJobs.fs, adapted locally per plan
-- decision: AlitaBot-only for now, not promoted to BotInfra — see docs/TECH-DEBT.md).
CREATE TABLE scheduled_job (
    job_name          TEXT        PRIMARY KEY,
    last_completed_at TIMESTAMPTZ,
    locked_until      TIMESTAMPTZ,
    locked_by         TEXT
);

INSERT INTO scheduled_job (job_name) VALUES ('dossier_nightly_update');

GRANT SELECT, UPDATE ON scheduled_job TO alita_bot_service;

-- Individual memory facts extracted per person (nightly job, DossierService). Each row is
-- one short, dedup'd fact/observation, embedded for semantic similarity recall.
CREATE TABLE interaction_memory (
    id         BIGSERIAL    PRIMARY KEY,
    user_id    BIGINT       NOT NULL,
    content    TEXT         NOT NULL,
    embedding  vector(1536) NOT NULL,
    valid_from TIMESTAMPTZ  NOT NULL DEFAULT now(),
    valid_to   TIMESTAMPTZ  NULL,
    created_at TIMESTAMPTZ  NOT NULL DEFAULT now()
);

-- HNSW, cosine distance (`<=>`) — matches ResponderService's recall query and
-- DossierService's dedup query, same convention as message_embedding (V3 migration).
CREATE INDEX ix_interaction_memory_hnsw ON interaction_memory USING hnsw (embedding vector_cosine_ops);

-- Every real query filters to `valid_to IS NULL` ("active" facts) — a partial index keeps
-- both the dedup lookup and the recall/`/dossier` listing cheap as the table grows, and
-- naturally stays small even though nothing supersedes a fact yet (see the header comment).
CREATE INDEX ix_interaction_memory_active ON interaction_memory (user_id) WHERE valid_to IS NULL;

-- `/dossier`'s "newest 5 active facts" listing.
CREATE INDEX ix_interaction_memory_user_created ON interaction_memory (user_id, created_at DESC);

-- INSERT for new facts, UPDATE for a future supersede (`valid_to`), DELETE for `/forget-me`.
GRANT SELECT, INSERT, UPDATE, DELETE ON interaction_memory TO alita_bot_service;
GRANT USAGE, SELECT ON SEQUENCE interaction_memory_id_seq TO alita_bot_service;

-- One cumulative LLM-merged summary per person, refreshed nightly whenever new facts
-- landed for them (DossierService.RunNightlyUpdate).
CREATE TABLE person_dossier (
    user_id      BIGINT      PRIMARY KEY,
    display_name TEXT,
    summary      TEXT        NOT NULL,
    updated_at   TIMESTAMPTZ
);

GRANT SELECT, INSERT, UPDATE, DELETE ON person_dossier TO alita_bot_service;

-- `/forget-me`: a user who opts out is excluded from the nightly job, the inline embedding
-- pipeline, and recall injection (see BotService/DossierService/ResponderService) —
-- checked by user_id existence in this table, no TTL/expiry.
CREATE TABLE memory_opt_out (
    user_id      BIGINT PRIMARY KEY,
    opted_out_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

GRANT SELECT, INSERT ON memory_opt_out TO alita_bot_service;

-- `/forget-me` also hard-deletes the opted-out user's message_embedding rows (their
-- interaction_memory/person_dossier rows are covered by the grants above) — message_log
-- itself is intentionally left alone, it's the shared chat record, not personal memory.
GRANT DELETE ON message_embedding TO alita_bot_service;
