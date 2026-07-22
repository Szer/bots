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

-- Slice 6: Алита's real persona (RU) — cynical-warm chat-native personality for a
-- ~30-person IT chat, tuned for gpt-5-mini. Replaces the S1 placeholder ("дружелюбный
-- участник чата, отвечай кратко"). This is the DEV/TEST seed value; the deployed prod
-- value is tuned live via `bot_setting` and does NOT need to match this file exactly
-- (see AGENTS.md's "Settings seeds, not migrations" — settings are hand-run SQL, not
-- Flyway migrations) — treat this row as a starting point, not a source of truth for prod.
INSERT INTO bot_setting (key, value, type, feature_group, description) VALUES
    ('TARGET_CHAT_IDS',         '',                                                       'FREE_FORM',    'telegram',    'Comma/semicolon/space-separated chat ids the bot listens to (fill in your test group id)'),
    ('BOT_USERNAME',            'alita_test_bot',                                         'FREE_FORM',    'telegram',    'Bot username without @, used for mention trigger detection'),
    ('SYSTEM_PROMPT',           'Тебя зовут Алита. Ты не ассистент, а участница IT-чата примерно на 30 человек — свой человек в беседе, а не сервис поддержки. Характер: циничная, но не злая — тёплая ирония вместо холодного сарказма. По умолчанию — резкая и лаконичная, без "воды". Чёрный юмор уместен и приветствуется, но не на ровном месте и не в адрес того, кому реально плохо. Категорически запрещены ассистентские обороты: "отличный вопрос", "с удовольствием помогу", "надеюсь, это было полезно", любые дисклеймеры и извинения за то, что ты ИИ. Никакого emoji-спама — эмодзи если и есть, то одно и по делу, чаще вообще без них. Обращайся к людям по именам, которые видишь в истории переписки — ты помнишь, кто есть кто, и можешь сослаться на то, что кто-то говорил раньше или что записано в его досье. Если вопрос дурацкий — так и скажи, но по-свойски. Если не знаешь ответа — не выдумывай, честно признай. Пиши как живой человек в чате: короткими репликами, без вступлений и заключений, без нумерованных списков там, где хватит одной фразы.', 'FREE_FORM',    'llm',         'System prompt for the LLM responder — Алита persona (Slice 6); prod value tuned live via bot_setting'),
    ('RESPONDER_MODE',          'echo',                                                   'FREE_FORM',    'llm',         'echo | llm'),
    ('STREAM_MODE',             'edit',                                                   'FREE_FORM',    'llm',         'draft | edit | plain — edit is the default: sendMessageDraft (Bot API 10.x) is rejected outright (400 TEXTDRAFT_PEER_INVALID) in basic groups, the bot''s primary surface; see src/AlitaBot/README.md'),
    ('CONTEXT_WINDOW_MESSAGES', '30',                                                     'FREE_FORM',    'llm',         'How many recent message_log rows feed the LLM context'),
    ('AZURE_FOUNDRY_ENDPOINT',  'https://szer-foundry.cognitiveservices.azure.com',       'FREE_FORM',    'llm',         'Azure AI Foundry resource base URL'),
    ('LLM_DEPLOYMENT',          'alita-gpt-5-mini',                                       'FREE_FORM',    'llm',         'Chat-completions deployment name'),
    ('EMBEDDING_DEPLOYMENT',    'alita-text-embedding-3-small',                           'FREE_FORM',    'llm',         'Embeddings deployment name'),
    ('STT_DEPLOYMENT',          'alita-stt',                                              'FREE_FORM',    'llm',         'Speech-to-text deployment name (audio/transcriptions)'),
    ('TTS_DEPLOYMENT',          'alita-tts',                                              'FREE_FORM',    'llm',         'Text-to-speech deployment name (audio/speech)'),
    ('LLM_PRICING',             '{"gpt-5-mini":{"input_per_1m":0.25,"output_per_1m":2.00},"alita-image":{"per_image_low":0.02,"per_image_medium":0.04,"per_image_high":0.08}}', 'JSON_BLOB', 'llm', 'USD prices per 1M tokens by model (chat/embeddings) and per-image by quality tier (image gen), for cost telemetry'),
    ('VOICE_TRANSCRIBE_ENABLED', 'true',                                                  'FEATURE_FLAG', 'llm',        'Auto-transcribe Voice/VideoNote/Audio messages in target chats'),
    ('VISION_ENABLED',          'true',                                                   'FEATURE_FLAG', 'llm',        'Attach photos (triggering message and/or its reply target) as image_url parts on LLM requests'),
    ('VISION_DETAIL',           'low',                                                    'FREE_FORM',    'llm',        'OpenAI image_url detail hint: low | high — controls vision token cost'),
    ('IMAGE_DEPLOYMENT',        '',                                                       'FREE_FORM',    'llm',        'images/generations + images/edits deployment name (fill in once quota is granted — see AlitaBot/docs/TECH-DEBT.md; /img fails gracefully while empty)'),
    ('IMAGE_GEN_ENABLED',       'true',                                                   'FEATURE_FLAG', 'llm',        'Enables the /img and !img commands'),
    ('IMAGE_SIZE',              '1024x1024',                                              'FREE_FORM',    'llm',        'images/generations size param'),
    ('IMAGE_QUALITY',           'medium',                                                 'FREE_FORM',    'llm',        'images/generations quality param: low | medium | high'),
    ('MODEL_ALLOWLIST',         '["alita-gpt-5-mini"]',                                   'JSON_BLOB',    'llm',        '/model may switch LLM_DEPLOYMENT to any deployment name in this array'),
    ('SUMMARY_PROMPT',          'Подведи итоги обсуждения в этом чате: с лёгким сарказмом, по темам, кто что утверждал. Коротко, без длинных вступлений.', 'FREE_FORM', 'llm', 'System prompt for the /summary command'),
    ('TEST_MODE',               'false',                                                  'FEATURE_FLAG', 'diagnostics', 'Enables test-only endpoints (e.g. /test/clock/advance)'),
    ('EMBED_MESSAGES',          'true',                                                   'FEATURE_FLAG', 'llm',        'Embed every logged message (user + bot) into message_embedding for /ask semantic search'),
    ('EMBEDDING_MIN_CHARS',     '3',                                                      'FREE_FORM',    'llm',        'Messages shorter than this are never embedded'),
    ('ASK_TOP_K',               '8',                                                      'FREE_FORM',    'llm',        '/ask: how many nearest message_embedding rows to pull as candidate context before the similarity floor'),
    ('ASK_SIM_FLOOR',           '0.5',                                                    'FREE_FORM',    'llm',        '/ask: minimum cosine similarity (1 - cosine distance) for a candidate to be included in the context'),
    ('ASK_PROMPT',              'Ты отвечаешь на вопрос по истории этого чата. Ниже — релевантные сообщения (автор, дата, текст). Сформулируй краткий связный ответ СВОИМИ СЛОВАМИ, не копируй сообщения дословно, и укажи в скобках, кто и когда это сказал. Опирайся только на приведённые сообщения. Если ответа в них нет — прямо скажи, что ничего подходящего не нашла, не выдумывай.', 'FREE_FORM', 'llm', 'System prompt for the /ask command'),
    ('DOSSIER_ENABLED',         'true',                                                   'FEATURE_FLAG', 'llm',        'Recall injection (dossier summary + matching facts appended to the LLM system prompt) — the nightly extraction job itself always runs regardless'),
    ('DOSSIER_RECALL_K',        '5',                                                      'FREE_FORM',    'llm',        'How many nearest active interaction_memory facts to recall for a triggering message''s author'),
    ('DOSSIER_SIM_FLOOR',       '0.60',                                                   'FREE_FORM',    'llm',        'Minimum cosine similarity for a recalled fact'),
    ('EXTRACT_PROMPT',          'Ты анализируешь недавние сообщения одного человека в чате и выделяешь новые факты о нём, которых ещё нет в текущем досье: черты характера, мнения, симпатии/антипатии, увлечения, повторяющиеся темы, заметные цитаты. Ответь ТОЛЬКО JSON-массивом коротких строк-фактов на русском языке, каждая — отдельный новый факт. Если новых фактов нет — верни []. Пример: ["любит кофе по утрам", "не любит YAML", "пишет на F#"]', 'FREE_FORM', 'llm', 'System prompt for the nightly fact-extraction LLM call (DossierService) — must yield a JSON array of short fact strings'),
    ('MERGE_PROMPT',            'Ты ведёшь растущее личное досье участника приватного Telegram-чата. Объедини текущее досье с новыми фактами в единый связный абзац на русском языке. Не удаляй и не противоречь существующей информации, если она явно не устарела. Кратко — не более 250 слов. Ответь только текстом обновлённого досье, без вступлений и пояснений.', 'FREE_FORM', 'llm', 'System prompt for the nightly summary-merge LLM call (DossierService) — must answer in RU, max 250 words'),
    -- Slice 6: rewriter pass (OFF by default — see AlitaBot/README.md "Rewriter pass"),
    -- outcome router (default weights keep the pre-S6 always-reply behavior; prod tuning
    -- happens live via bot_setting, same posture as SYSTEM_PROMPT above).
    ('REWRITER_ENABLED',        'false',                                                  'FEATURE_FLAG', 'llm',        'Second, cheap non-stream LLM call rewrites the final reply text before rendering ("перепиши как живой человек в чате")'),
    ('REWRITER_PROMPT',         'Перепиши следующий ответ так, как будто его написал живой человек в чате: убери ассистентские обороты и канцелярит, сократи, где можно, сохрани смысл и все факты. Ответь только переписанным текстом, без пояснений.', 'FREE_FORM', 'llm', 'System prompt for the rewriter pass (only used when REWRITER_ENABLED=true)'),
    ('OUTCOME_WEIGHTS',         '{"reply":100,"silence":0,"emoji":0}',                    'JSON_BLOB',    'llm',        'Weighted roll for a TRIGGERED non-command message: reply (normal path) | silence (say nothing) | emoji (react instead of replying) — see Services/OutcomeRouter.fs')
ON CONFLICT (key) DO NOTHING;
