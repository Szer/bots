-- UserBanned/day and VahterActed/day (grouped by moderator action type),
-- last 28 days. `event` only — legacy `banned`/`banned_by_bot`/`vahter_actions`
-- froze 2026-04-02 (Flyway V23 event-sourcing cutover).
SELECT
    date_trunc('day', created_at)::date AS day,
    'UserBanned' AS event_type,
    NULL::text AS action_type,
    count(*) AS event_count
FROM event
WHERE event_type = 'UserBanned'
  AND created_at >= NOW() - INTERVAL '28 days'
GROUP BY day

UNION ALL

SELECT
    date_trunc('day', created_at)::date AS day,
    'VahterActed' AS event_type,
    data->'actionType'->>'Case' AS action_type,
    count(*) AS event_count
FROM event
WHERE event_type = 'VahterActed'
  AND created_at >= NOW() - INTERVAL '28 days'
GROUP BY day, action_type

ORDER BY day DESC, event_type, action_type;
