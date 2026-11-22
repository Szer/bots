INSERT INTO bot_setting (key, value, type, feature_group, description)
VALUES ('MANAGED_INVITES_ENABLED', 'false', 'FEATURE_FLAG', 'COMMUNITY', 'Issue personal single-use community invitations');

ALTER TABLE "user"
    ADD COLUMN invited_by bigint REFERENCES "user"(id),
    ADD COLUMN invite_link text;

CREATE TABLE community_invite_event (
    id bigserial PRIMARY KEY,
    stream_id text NOT NULL,
    stream_version int NOT NULL CHECK (stream_version > 0),
    event_type text GENERATED ALWAYS AS (data->>'Case') STORED NOT NULL,
    data jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (stream_id, stream_version)
);

CREATE UNIQUE INDEX community_invite_event_command ON community_invite_event
    (stream_id, ((data->>'messageId')::bigint)) WHERE event_type='invite_link_obtained';

CREATE UNIQUE INDEX community_invite_event_creation ON community_invite_event
    ((data->>'inviteId')) WHERE event_type='invite_link_created';
CREATE UNIQUE INDEX community_invite_event_join ON community_invite_event
    (stream_id, (data->>'inviteId'), ((data->>'updateId')::bigint), created_at) WHERE event_type='invite_link_redeemed';
CREATE INDEX community_invite_event_latest_join ON community_invite_event
    (((data->>'chatId')::bigint), ((data->>'actorId')::bigint), created_at DESC, ((data->>'updateId')::bigint) DESC)
    WHERE event_type='invite_link_redeemed';

CREATE FUNCTION community_invite_key(link text) RETURNS text LANGUAGE sql IMMUTABLE STRICT
AS $$ SELECT encode(sha256(convert_to(link,'UTF8')), 'hex') $$;

GRANT SELECT, INSERT ON community_invite_event TO coupon_hub_bot_service;
GRANT USAGE, SELECT ON SEQUENCE community_invite_event_id_seq TO coupon_hub_bot_service;
