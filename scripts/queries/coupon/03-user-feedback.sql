-- user_feedback submissions per week, last 12 weeks. SECONDARY signal only —
-- 0 rows since 2026-05-10 against a live community chat. Must degrade to a
-- no-op when empty rather than being the opening step (see product.md).
SELECT
    date_trunc('week', created_at)::date AS week,
    count(*) AS feedback_count
FROM user_feedback
WHERE created_at >= NOW() - INTERVAL '84 days'
GROUP BY week
ORDER BY week DESC;
