-- message_log rows per day, last 28 days. Reported WITHOUT judgement —
-- alita's traffic_class is `dormant`; 0 rows on most days is normal, not an
-- anomaly. Not consumed by product.sh (alita has no `product` role); kept
-- here for the future monitor role (Phase 2).
SELECT
    date_trunc('day', sent_at)::date AS day,
    count(*) AS message_count
FROM message_log
WHERE sent_at >= NOW() - INTERVAL '28 days'
GROUP BY day
ORDER BY day DESC;
