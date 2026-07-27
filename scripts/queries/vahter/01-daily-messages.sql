-- MessageReceived volume and unique senders per day, last 28 days.
-- `event` only — the legacy `message`/`user` tables froze 2026-04-02
-- (Flyway V23 event-sourcing cutover) and would silently return stale data.
SELECT
    date_trunc('day', created_at)::date AS day,
    count(*) AS messages_received,
    count(DISTINCT (data->>'userId')::bigint) AS unique_senders
FROM event
WHERE event_type = 'MessageReceived'
  AND created_at >= NOW() - INTERVAL '28 days'
GROUP BY day
ORDER BY day DESC;
