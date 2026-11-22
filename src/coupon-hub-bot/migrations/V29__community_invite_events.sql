LOCK TABLE community_invite, community_invite_join IN SHARE ROW EXCLUSIVE MODE;

ALTER TABLE community_invite ADD COLUMN invite_id uuid NOT NULL DEFAULT gen_random_uuid() UNIQUE;

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

INSERT INTO community_invite_event(stream_id, stream_version, created_at, data)
SELECT 'coupon-invite:' || i.chat_id || ':' || i.sponsor,
       row_number() OVER (PARTITION BY i.chat_id, i.sponsor ORDER BY j.joined_at, j.update_id, i.invite_id),
       j.joined_at, jsonb_build_object('Case','invite_link_redeemed','inviteId',i.invite_id,
           'chatId',i.chat_id,'sponsorId',i.sponsor,'actorKind','user','actorId',j.applicant,'updateId',j.update_id)
FROM community_invite_join j JOIN community_invite i USING (link);

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
