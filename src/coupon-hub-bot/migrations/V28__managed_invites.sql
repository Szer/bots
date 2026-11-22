INSERT INTO bot_setting (key, value, type, feature_group, description)
VALUES ('MANAGED_INVITES_ENABLED', 'false', 'FEATURE_FLAG', 'COMMUNITY', 'Issue personal single-use community invitations');

ALTER TABLE "user" ADD COLUMN invited_by bigint REFERENCES "user"(id);
CREATE TABLE community_invite (
    link text PRIMARY KEY,
    invite_id uuid NOT NULL DEFAULT gen_random_uuid() UNIQUE,
    chat_id bigint NOT NULL,
    sponsor bigint NOT NULL REFERENCES "user"(id),
    consumed boolean NOT NULL DEFAULT false
);
CREATE UNIQUE INDEX community_invite_available ON community_invite(chat_id, sponsor) WHERE NOT consumed;

CREATE TABLE community_invite_join (
    link text NOT NULL REFERENCES community_invite(link),
    applicant bigint NOT NULL REFERENCES "user"(id),
    joined_at timestamptz NOT NULL,
    update_id bigint NOT NULL,
    PRIMARY KEY (link, update_id, joined_at)
);
CREATE INDEX community_invite_join_applicant ON community_invite_join(applicant, joined_at DESC, update_id DESC);
GRANT SELECT, INSERT, UPDATE ON community_invite TO coupon_hub_bot_service;
GRANT SELECT, INSERT ON community_invite_join TO coupon_hub_bot_service;

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

CREATE FUNCTION append_community_invite_event(invite community_invite, action text, details jsonb, occurred_at timestamptz)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    stream text := 'coupon-invite:' || invite.chat_id || ':' || invite.sponsor;
BEGIN
    PERFORM id FROM "user" WHERE id=invite.sponsor FOR NO KEY UPDATE;
    INSERT INTO community_invite_event(stream_id, stream_version, created_at, data)
    SELECT stream, COALESCE(MAX(stream_version), 0) + 1, occurred_at,
           details || jsonb_build_object('Case',action,'inviteId',invite.invite_id,'chatId',invite.chat_id,'sponsorId',invite.sponsor)
    FROM community_invite_event WHERE stream_id=stream;
END;
$$;

CREATE FUNCTION record_community_invite_created() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM append_community_invite_event(NEW, 'invite_link_created', '{"actorKind":"bot"}', clock_timestamp());
    RETURN NEW;
END;
$$;

CREATE FUNCTION record_community_invite_redeemed() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM append_community_invite_event(i, 'invite_link_redeemed',
        jsonb_build_object('actorKind','user','actorId',NEW.applicant,'updateId',NEW.update_id), NEW.joined_at)
    FROM community_invite i WHERE i.link=NEW.link;
    RETURN NEW;
END;
$$;

CREATE TRIGGER community_invite_created AFTER INSERT ON community_invite
    FOR EACH ROW EXECUTE FUNCTION record_community_invite_created();
CREATE TRIGGER community_invite_redeemed AFTER INSERT ON community_invite_join
    FOR EACH ROW EXECUTE FUNCTION record_community_invite_redeemed();

GRANT SELECT, INSERT ON community_invite_event TO coupon_hub_bot_service;
GRANT USAGE, SELECT ON SEQUENCE community_invite_event_id_seq TO coupon_hub_bot_service;
