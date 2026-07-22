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
    -- gemini-3.1-flash-image/lyria-3-pro per-item prices are ESTIMATES (never empirically
    -- billed — this key's Google Cloud project has 0 free-tier quota for image/music
    -- generateContent, see GeminiProvider.fs's doc comment) — update once a real invoice
    -- confirms actual per-item cost.
    ('LLM_PRICING',             '{"gpt-5-mini":{"input_per_1m":0.25,"output_per_1m":2.00},"alita-image":{"per_image_low":0.02,"per_image_medium":0.04,"per_image_high":0.08},"gemini-3.1-flash-image":{"per_image":0.02},"lyria-3-pro":{"per_track":0.06}}', 'JSON_BLOB', 'llm', 'USD prices per 1M tokens by model (chat/embeddings), per-image by quality tier or flat (image gen), and per-track (music gen), for cost telemetry'),
    ('VOICE_TRANSCRIBE_ENABLED', 'true',                                                  'FEATURE_FLAG', 'llm',        'Auto-transcribe Voice/VideoNote/Audio messages in target chats'),
    ('VISION_ENABLED',          'true',                                                   'FEATURE_FLAG', 'llm',        'Attach photos (triggering message and/or its reply target) as image_url parts on LLM requests'),
    ('VISION_DETAIL',           'low',                                                    'FREE_FORM',    'llm',        'OpenAI image_url detail hint: low | high — controls vision token cost'),
    ('IMAGE_DEPLOYMENT',        '',                                                       'FREE_FORM',    'llm',        'images/generations + images/edits deployment name (fill in once quota is granted — see AlitaBot/docs/TECH-DEBT.md; /img fails gracefully while empty)'),
    ('IMAGE_GEN_ENABLED',       'true',                                                   'FEATURE_FLAG', 'llm',        'Enables the /img and !img commands'),
    ('IMAGE_SIZE',              '1024x1024',                                              'FREE_FORM',    'llm',        'images/generations size param'),
    ('IMAGE_QUALITY',           'medium',                                                 'FREE_FORM',    'llm',        'images/generations quality param: low | medium | high'),
    ('LLM_MODELS',              '[{"model":"gpt-5-mini","deployment":"alita-gpt-5-mini"}]', 'JSON_BLOB',  'llm',        '/model''s model catalog: shows and accepts these "model" names verbatim (never the "deployment" id, which is this bot''s namespacing convention on the shared Foundry account); switching sets LLM_DEPLOYMENT to the matched entry''s "deployment"'),
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
    ('OUTCOME_WEIGHTS',         '{"reply":100,"silence":0,"emoji":0}',                    'JSON_BLOB',    'llm',        'Weighted roll for a TRIGGERED non-command message: reply (normal path) | silence (say nothing) | emoji (react instead of replying) — see Services/OutcomeRouter.fs'),
    -- Slice 7: social engine (/roast, /awards, /quote, /karma). Zero-censorship, dark-humor-
    -- normal ~30-person IT chat — prompts are deliberately sharp, not corporate-soft; the bot
    -- only roasts on explicit command.
    ('ROAST_PROMPT',            'Прожарь этого человека как злой стендапер — конкретно, по фактам из досье и его же цитатам, цитируй дословно где смешнее, без дисклеймеров и без смягчений. 4-6 предложений.', 'FREE_FORM', 'llm', 'System prompt for the /roast command'),
    ('ROAST_COOLDOWN_SECONDS',  '300',                                                    'FREE_FORM',    'llm',        'Minimum seconds between two /roast calls against the same target'),
    ('AWARDS_PROMPT',           'Ты подводишь шутливые итоги недели в IT-чате. Ниже — сообщения за последние 7 дней, каждое подписано автором в формате [обращение]: текст. Выбери 3-5 участников и придумай каждому меткий, остроумный титул (например «Душнила недели», «Пророк», или свой) на основе того, что они писали. Ответь ТОЛЬКО строгим JSON-массивом объектов вида {"title": "...", "user": "...", "evidence_quote": "..."} — user должен быть ТОЛЬКО самим обращением (@handle или имя) БЕЗ квадратных скобок вокруг него, evidence_quote — короткая дословная цитата, подтверждающая титул. Никакого текста вне JSON.', 'FREE_FORM', 'llm', 'System prompt for the /awards command — must yield a strict JSON array of {title,user,evidence_quote}, user WITHOUT surrounding [] brackets'),
    ('QUOTE_PROMPT',            'Ниже — сообщения этого чата за последние сутки, каждое подписано автором. Выбери ОДНУ, самую абсурдную или цитируемую реплику. Ответь ТОЛЬКО строгим JSON-объектом вида {"author": "...", "quote": "...", "comment": "..."} — quote дословно, comment — короткий ироничный комментарий на русском. Никакого текста вне JSON.', 'FREE_FORM', 'llm', 'System prompt for the /quote command — must yield a strict JSON object {author,quote,comment}'),
    -- Slice 8: proactive behavior (morning digest, willingness-gated interjections, meme
    -- reactions). DESIGN PRINCIPLE: every one of these defaults OFF/0.0 here — the bot
    -- stays polite and silent until these are hand-tuned live via bot_setting in prod
    -- (see AGENTS.md's "Settings seeds, not migrations"); tests enable them per-test via
    -- the same SetBotSetting + /reload-settings mechanism.
    ('DIGEST_ENABLED',          'false',                                                  'FEATURE_FLAG', 'proactive',  'Daily morning digest job (digest_daily) — the job always runs on schedule, this only gates whether it actually sends anything'),
    ('DIGEST_UTC_HOUR',         '7',                                                      'FREE_FORM',    'proactive',  'UTC hour the digest job is scheduled to run at daily'),
    ('DIGEST_MIN_MESSAGES',     '30',                                                     'FREE_FORM',    'proactive',  'Minimum human, non-command message_log rows in the last 24h for a target chat to get a digest'),
    ('DIGEST_PROMPT',           'Сделай утренний дайджест вчерашнего срача — по темам, с лёгким сарказмом, кто что задвигал. 6-10 строк, без воды.', 'FREE_FORM', 'proactive', 'System prompt for the morning-digest LLM call'),
    ('INTERJECT_PROBABILITY',   '0.0',                                                    'FREE_FORM',    'proactive',  'Roll (0..1) gating a willingness-gated interjection on a non-triggered, non-command text message — 0.0 disables interjections entirely'),
    ('BURST_MSGS',              '8',                                                      'FREE_FORM',    'proactive',  'Minimum non-bot messages in the burst window for an interjection to be eligible'),
    ('BURST_SPEAKERS',          '3',                                                      'FREE_FORM',    'proactive',  'Minimum distinct speakers in the burst window'),
    ('BURST_WINDOW_MINUTES',    '5',                                                      'FREE_FORM',    'proactive',  'Lookback window (minutes) for the burst check'),
    ('INTERJECT_COOLDOWN_MINUTES', '30',                                                  'FREE_FORM',    'proactive',  'Minimum minutes since the bot''s last message in a chat (reply OR a previous interjection) before another interjection is allowed'),
    ('INTERJECT_PROMPT',        'Можешь вставить ОДНУ меткую реплику в этот разговор, или ответь ровно PASS если нечего добавить.', 'FREE_FORM', 'proactive', 'System prompt for the interjection LLM call — must answer exactly PASS (trim/case-insensitive) when it has nothing to add'),
    ('MEME_REACT_PROBABILITY',  '0.0',                                                    'FREE_FORM',    'proactive',  'Roll (0..1) gating a meme reaction on a non-triggered photo message — 0.0 disables meme reactions entirely'),
    ('MEME_REACT_PROMPT',       'Оцени фото. Ответь ТОЛЬКО строгим JSON {"action":"react|comment|pass","emoji":"...","text":"..."} — react: одно эмодзи-реакция по делу; comment: одна короткая ироничная реплика; pass: если фото не заслуживает реакции. Никакого текста вне JSON.', 'FREE_FORM', 'proactive', 'System prompt for the meme-reaction vision LLM call — must answer strict JSON {action,emoji,text}'),
    -- Slice 9 (stretch): /say voice replies, admin-gated /sql analytics, cost footer.
    ('TTS_DEFAULT_VOICE',       'alloy',                                                  'FREE_FORM',    'llm',        '/say default voice when no explicit voice arg is given'),
    ('SAY_MAX_CHARS',           '500',                                                    'FREE_FORM',    'llm',        '/say refuses (RU) text longer than this instead of synthesizing it'),
    ('ADMIN_USER_IDS',          '[]',                                                     'JSON_BLOB',    'admin',      'Telegram user ids allowed to run /sql — empty until hand-seeded (see AGENTS.md''s "Settings seeds, not migrations")'),
    ('SQL_PROMPT',              'Ты переводишь вопрос на естественном языке в ОДИН read-only SQL SELECT-запрос к базе данных бота Алита (PostgreSQL). Таблицы: message_log(id, chat_id, message_id, user_id, username, display_name, is_bot, reply_to_message_id, text, sent_at) — весь текстовый лог чата; message_embedding(message_log_id, embedding vector(1536), embedded_at) — эмбеддинги сообщений; interaction_memory(id, user_id, content, embedding, valid_from, valid_to, created_at) — извлечённые факты о людях (valid_to IS NULL = активный факт); person_dossier(user_id, display_name, summary, updated_at) — досье по человеку; llm_usage(id, called_at, kind, model, input_tokens, output_tokens, cost_usd, chat_id, user_id) — учёт вызовов LLM; karma(id, user_id, username, title, evidence, awarded_at) — награды /awards; bot_setting(key, value, type, feature_group, description, created_at, updated_at) — настройки бота; scheduled_job(job_name, last_completed_at, locked_until, locked_by) — фоновые задачи. Отвечай ТОЛЬКО строгим JSON {"sql": "..."} с одним SELECT- или WITH-запросом (без точки с запятой внутри, без INSERT/UPDATE/DELETE/DROP/ALTER/CREATE/GRANT). Никакого текста вне JSON.', 'FREE_FORM', 'llm', 'System prompt for /sql — must yield strict JSON {"sql": "..."} with a single read-only SELECT/WITH statement against Alita''s own schema'),
    ('COST_FOOTER_ENABLED',     'false',                                                  'FEATURE_FLAG', 'llm',        'Appends a "⛽ $0.0021" cost line to LLM responder replies (not command replies) — visible in Telegram, stripped before the message_log insert'),
    -- Gemini provider slice: Nano Banana images (/img, IMAGE_PROVIDER-routed) + Lyria
    -- music (/song). GEMINI_API_KEY is a secret (env var, like AZURE_FOUNDRY_KEY) — never
    -- a bot_setting. Model names are the discovered roster (see GeminiProvider.fs's doc
    -- comment) as of 2026-07-22; Azure image quota is still 0 (AlitaBot/docs/TECH-DEBT.md)
    -- so IMAGE_PROVIDER defaults to gemini until that lands.
    ('GEMINI_BASE_URL',         'https://generativelanguage.googleapis.com',             'FREE_FORM',    'llm',        'Google Generative Language API base URL'),
    ('GEMINI_IMAGE_MODEL',      'gemini-3.1-flash-image',                                'FREE_FORM',    'llm',        '/img (IMAGE_PROVIDER=gemini) generateContent model — "Nano Banana 2"'),
    ('GEMINI_MUSIC_MODEL',      'lyria-3-pro-preview',                                   'FREE_FORM',    'llm',        '/song generateContent model'),
    ('IMAGE_PROVIDER',          'gemini',                                                'FREE_FORM',    'llm',        '/img backend: azure | gemini'),
    ('SONG_MAX_CHARS',          '1000',                                                  'FREE_FORM',    'llm',        '/song refuses (RU) a style+lyrics prompt longer than this'),
    ('SONG_COOLDOWN_SECONDS',   '120',                                                   'FREE_FORM',    'llm',        'Minimum seconds between two /song calls from the same user')
ON CONFLICT (key) DO NOTHING;
