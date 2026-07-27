-- LlmClassified verdict mix (LLM triage) and MlScoredMessage spam-flag
-- volume, per day, last 28 days. `event` only — legacy `llm_triage` froze
-- 2026-04-02 (Flyway V23 event-sourcing cutover).
SELECT
    date_trunc('day', created_at)::date AS day,
    'LlmClassified' AS source,
    data->>'verdict' AS bucket,
    count(*) AS event_count
FROM event
WHERE event_type = 'LlmClassified'
  AND created_at >= NOW() - INTERVAL '28 days'
GROUP BY day, bucket

UNION ALL

SELECT
    date_trunc('day', created_at)::date AS day,
    'MlScoredMessage' AS source,
    'isSpam' AS bucket,
    count(*) FILTER (WHERE (data->>'isSpam')::bool) AS event_count
FROM event
WHERE event_type = 'MlScoredMessage'
  AND created_at >= NOW() - INTERVAL '28 days'
GROUP BY day

ORDER BY day DESC, source, bucket;
