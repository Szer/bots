-- ML correction rate: MessageMarkedHam + MessageMarkedSpam per day (a human
-- moderator overriding the ML/LLM verdict after the fact), last 28 days.
SELECT
    date_trunc('day', created_at)::date AS day,
    event_type,
    count(*) AS event_count
FROM event
WHERE event_type IN ('MessageMarkedHam', 'MessageMarkedSpam')
  AND created_at >= NOW() - INTERVAL '28 days'
GROUP BY day, event_type
ORDER BY day DESC, event_type;
