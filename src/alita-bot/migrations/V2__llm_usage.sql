-- Persistent usage/cost accounting (Phase-1 Slice 4). Additive to the existing OTel
-- metrics (alitabot_llm_tokens_total / alitabot_llm_cost_usd_total, src/AlitaBot/Telemetry.fs)
-- -- this table is what /usage reads to render historical totals without a metrics backend.
-- One row per successful LLM/STT/TTS/image call, written from the provider telemetry path
-- (AlitaBot.Llm.LlmTelemetry's LlmCall/ImageCall via IUsageRecorder).
CREATE TABLE llm_usage (
    id            BIGSERIAL   PRIMARY KEY,
    called_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    kind          TEXT        NOT NULL
                  CHECK (kind IN ('chat', 'stt', 'tts', 'image', 'embedding')),
    model         TEXT        NOT NULL,
    input_tokens  INT,                     -- NULL when the call type reports no usage (e.g. STT)
    output_tokens INT,
    cost_usd      NUMERIC(10, 6),          -- NULL when LLM_PRICING has no matching entry
    chat_id       BIGINT,                  -- NULL when the call has no chat context
    user_id       BIGINT                   -- NULL when the call has no user context
);

CREATE INDEX ix_llm_usage_called_at ON llm_usage (called_at DESC);

GRANT SELECT, INSERT ON llm_usage TO alita_bot_service;
GRANT USAGE, SELECT ON SEQUENCE llm_usage_id_seq TO alita_bot_service;
