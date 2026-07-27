-- Verbatim recent chat text for product-signal mining, last 14 days.
-- `data->>'text'` where event_type='MessageReceived' is the only live text
-- source — the legacy `message` table froze 2026-04-02.
SELECT
    to_char(created_at, 'YYYY-MM-DD HH24:MI') AS ts,
    (data->>'chatId') AS chat_id,
    LEFT(md5(data->>'userId'), 8) AS user_hash,
    CASE WHEN data->>'text' IS NOT NULL
         THEN REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(LEFT(data->>'text', 200), E'\n', ' '), E'\t', ' '), '|', '/'), '<', '('), '>', ')')
         ELSE '(no text)'
    END AS preview
FROM event
WHERE event_type = 'MessageReceived'
  AND created_at >= NOW() - INTERVAL '14 days'
ORDER BY created_at DESC
LIMIT 200;
