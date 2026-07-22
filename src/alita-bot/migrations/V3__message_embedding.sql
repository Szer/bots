-- Memory foundation (Phase-1 Slice 5a): per-message embeddings for semantic /ask.
-- `vector` is a "trusted" extension (pgvector >= 0.6) — installable by the DB owner
-- (the `admin` role Flyway runs as, per init.sql) without superuser.
CREATE EXTENSION IF NOT EXISTS vector;

-- One row per embedded message_log row (both user and bot messages — see
-- BotService's embedding pipeline). ON DELETE CASCADE: a message_log row can never
-- outlive its embedding as an orphan.
CREATE TABLE message_embedding (
    message_log_id BIGINT      PRIMARY KEY REFERENCES message_log(id) ON DELETE CASCADE,
    embedding       vector(1536) NOT NULL,
    embedded_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- HNSW, cosine distance (`<=>`) — matches the operator /ask's semantic search uses.
CREATE INDEX ix_message_embedding_hnsw ON message_embedding USING hnsw (embedding vector_cosine_ops);

-- PK is a FK to an existing bigint column, not a local sequence — no sequence grant needed.
GRANT SELECT, INSERT ON message_embedding TO alita_bot_service;
