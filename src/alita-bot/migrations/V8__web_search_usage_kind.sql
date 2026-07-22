-- S10 PR1: natural-language tool-calling loop — web_search sub-calls (Azure Responses API)
-- record llm_usage rows like every other LLM call. Adds 'web_search' to llm_usage.kind's
-- CHECK. No new table for the tool-loop rate limit — DbService.ToolCallCountSince queries
-- llm_usage directly (kind IN ('image','music','web_search'), same table every other
-- usage/cost query already reads).

ALTER TABLE llm_usage DROP CONSTRAINT llm_usage_kind_check;
ALTER TABLE llm_usage ADD CONSTRAINT llm_usage_kind_check
    CHECK (kind IN ('chat', 'stt', 'tts', 'image', 'embedding', 'music', 'web_search'));
