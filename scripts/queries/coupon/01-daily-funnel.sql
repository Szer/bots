-- Coupon lifecycle funnel per day (added/taken/used/returned/voided) plus
-- unique actor count per day, last 28 days. `coupon_event` is the dense
-- signal — not `chat_message`, which is bursty and has zero-days.
SELECT
    date_trunc('day', created_at)::date AS day,
    event_type,
    count(*) AS event_count,
    count(DISTINCT user_id) AS unique_users
FROM coupon_event
WHERE created_at >= NOW() - INTERVAL '28 days'
GROUP BY day, event_type
ORDER BY day DESC, event_type;
