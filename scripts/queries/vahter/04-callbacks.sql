-- CallbackCreated vs its terminal states per day, last 28 days.
-- CallbackResolved (a human clicked it) and CallbackExpired (nobody did, by
-- design — most confirmation prompts ship 2-3 sibling buttons and only ONE
-- can ever be clicked, so the other 1-2 are SUPPOSED to expire) are BOTH
-- terminal. Comparing CallbackCreated to CallbackResolved alone reads as a
-- permanently growing backlog even when nothing is stuck: issue #322 did
-- exactly that (30-day reconciliation showed 10,124 created vs 10,159
-- resolved+expired — no backlog at all). `outstanding` below is
-- created - resolved - expired for the SAME day; only a persistently
-- positive `outstanding` across multiple consecutive days is a real signal
-- of stuck callbacks, not a single day's value.
SELECT
    day,
    created,
    resolved,
    expired,
    resolved + expired AS resolved_or_expired,
    created - resolved - expired AS outstanding
FROM (
    SELECT
        date_trunc('day', created_at)::date AS day,
        count(*) FILTER (WHERE event_type = 'CallbackCreated') AS created,
        count(*) FILTER (WHERE event_type = 'CallbackResolved') AS resolved,
        count(*) FILTER (WHERE event_type = 'CallbackExpired') AS expired
    FROM event
    WHERE event_type IN ('CallbackCreated', 'CallbackResolved', 'CallbackExpired')
      AND created_at >= NOW() - INTERVAL '28 days'
    GROUP BY day
) daily
ORDER BY day DESC;
