-- llm_usage cost + call count per day, grouped by kind, last 28 days. Not
-- consumed by product.sh (alita has no `product` role); kept here for the
-- future monitor role (Phase 2).
SELECT
    date_trunc('day', called_at)::date AS day,
    kind,
    count(*) AS call_count,
    sum(cost_usd) AS total_cost_usd
FROM llm_usage
WHERE called_at >= NOW() - INTERVAL '28 days'
GROUP BY day, kind
ORDER BY day DESC, kind;
