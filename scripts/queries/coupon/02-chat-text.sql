-- Verbatim recent community chat text, last 14 days — the PRIMARY product
-- signal for this bot (see product.md: chat mining over /feedback triage,
-- which has had 0 rows since 2026-05-10).
SELECT
    to_char(created_at, 'YYYY-MM-DD HH24:MI') AS ts,
    chat_id,
    LEFT(md5(user_id::text), 8) AS user_hash,
    CASE WHEN text IS NOT NULL
         THEN REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(LEFT(text, 200), E'\n', ' '), E'\t', ' '), '|', '/'), '<', '('), '>', ')')
         ELSE '(media only)'
    END AS preview,
    reply_to_message_id
FROM chat_message
WHERE created_at >= NOW() - INTERVAL '14 days'
ORDER BY created_at DESC
LIMIT 200;
