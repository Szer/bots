-- Slice 7: social engine (/roast, /awards, /quote, /karma).

-- One row per awarded title (/awards writes one per entry in the LLM's JSON array).
-- user_id is nullable and deliberately best-effort: BotService.handleAwardsCommand only
-- resolves it when the LLM's "user" field looks like a "@username" handle it can match
-- against message_log.username (see AGENTS.md's "resolved from message_log usernames
-- where possible"); when it can't be resolved the row is still kept — with the raw
-- `username` text — so the announcement itself is never lost, just not queryable by
-- /karma <user> for that particular award.
CREATE TABLE karma (
    id         BIGSERIAL   PRIMARY KEY,
    user_id    BIGINT      NULL,
    username   TEXT,
    title      TEXT,
    evidence   TEXT,
    awarded_at TIMESTAMPTZ DEFAULT now()
);

-- /karma <user>'s lookup (count + newest titles).
CREATE INDEX ix_karma_user_id ON karma (user_id) WHERE user_id IS NOT NULL;

GRANT SELECT, INSERT ON karma TO alita_bot_service;
GRANT USAGE, SELECT ON SEQUENCE karma_id_seq TO alita_bot_service;

-- Per-target cooldown for /roast (ROAST_COOLDOWN_SECONDS bot_setting, default 300s) — one
-- row per person who has ever been roasted, upserted (not appended) since only the most
-- recent roast time matters for the cooldown check.
CREATE TABLE roast_cooldown (
    target_user_id  BIGINT      PRIMARY KEY,
    last_roasted_at TIMESTAMPTZ NOT NULL
);

GRANT SELECT, INSERT, UPDATE ON roast_cooldown TO alita_bot_service;
