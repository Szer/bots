-- Local-dev seed for bot_setting. Runs once via the `seed-bot-settings` compose
-- service AFTER Flyway has created the table in V1.
--
-- ON CONFLICT DO NOTHING so manual UPDATEs during a test session are not
-- clobbered on the next `docker compose up`. To force a refresh:
--   docker exec -it alita-bot-postgres-dev psql -U admin -d alita_bot \
--     -c "DELETE FROM bot_setting;"
--   docker compose -f src/alita-bot/docker-compose.dev.yml run --rm seed-bot-settings
--
-- Note: secrets (bot token, foundry key) still come from .env.local.

INSERT INTO bot_setting (key, value, type, feature_group, description) VALUES
    ('TARGET_CHAT_IDS',         '',                                                       'FREE_FORM',    'telegram',    'Comma/semicolon/space-separated chat ids the bot listens to (fill in your test group id)'),
    ('BOT_USERNAME',            'alita_test_bot',                                         'FREE_FORM',    'telegram',    'Bot username without @, used for mention trigger detection'),
    ('SYSTEM_PROMPT',           'Ты — Алита, дружелюбный участник чата. Отвечай кратко.', 'FREE_FORM',    'llm',         'System prompt for the LLM responder (placeholder)'),
    ('RESPONDER_MODE',          'echo',                                                   'FREE_FORM',    'llm',         'echo | llm'),
    ('STREAM_MODE',             'edit',                                                   'FREE_FORM',    'llm',         'draft | edit | plain'),
    ('CONTEXT_WINDOW_MESSAGES', '30',                                                     'FREE_FORM',    'llm',         'How many recent message_log rows feed the LLM context'),
    ('AZURE_FOUNDRY_ENDPOINT',  'https://szer-foundry.cognitiveservices.azure.com',       'FREE_FORM',    'llm',         'Azure AI Foundry resource base URL'),
    ('LLM_DEPLOYMENT',          'alita-gpt-5-mini',                                       'FREE_FORM',    'llm',         'Chat-completions deployment name'),
    ('EMBEDDING_DEPLOYMENT',    'alita-text-embedding-3-small',                           'FREE_FORM',    'llm',         'Embeddings deployment name'),
    ('LLM_PRICING',             '{"gpt-5-mini":{"input_per_1m":0.25,"output_per_1m":2.00}}', 'JSON_BLOB', 'llm',         'USD prices per 1M tokens by model, for cost telemetry'),
    ('TEST_MODE',               'false',                                                  'FEATURE_FLAG', 'diagnostics', 'Enables test-only endpoints (e.g. /test/clock/advance)')
ON CONFLICT (key) DO NOTHING;
