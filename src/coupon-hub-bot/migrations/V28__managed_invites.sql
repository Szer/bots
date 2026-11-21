INSERT INTO bot_setting (key, value, type, feature_group, description)
VALUES ('MANAGED_INVITES_ENABLED', 'false', 'FEATURE_FLAG', 'COMMUNITY', 'Issue personal single-use community invitations');

ALTER TABLE "user" ADD COLUMN invited_by bigint REFERENCES "user"(id);
CREATE TABLE community_invite (
    link text PRIMARY KEY,
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
