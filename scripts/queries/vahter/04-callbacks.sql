-- CallbackCreated vs CallbackResolved per day, last 28 days. A growing gap
-- between the two (created without a matching resolved) is the signal to
-- watch — stuck confirmation callbacks.
SELECT
    date_trunc('day', created_at)::date AS day,
    event_type,
    count(*) AS event_count
FROM event
WHERE event_type IN ('CallbackCreated', 'CallbackResolved')
  AND created_at >= NOW() - INTERVAL '28 days'
GROUP BY day, event_type
ORDER BY day DESC, event_type;
