INSERT INTO bot_setting (key, value, type, feature_group, description)
VALUES ('MANAGED_INVITES_ENABLED', 'false', 'FEATURE_FLAG', 'COMMUNITY', 'Issue personal single-use community invitations');

ALTER TABLE "user" ADD COLUMN invited_by bigint REFERENCES "user"(id);
CREATE TABLE community_invite (
    link text PRIMARY KEY,
    chat_id bigint NOT NULL,
    sponsor bigint NOT NULL REFERENCES "user"(id),
    applicant bigint,
    request_update bigint,
    new_user boolean NOT NULL DEFAULT false,
    retired boolean NOT NULL DEFAULT false,
    finished boolean NOT NULL DEFAULT false,
    CHECK ((applicant IS NULL) = (request_update IS NULL))
);
CREATE UNIQUE INDEX community_invite_available ON community_invite(chat_id, sponsor) WHERE applicant IS NULL;
GRANT SELECT, INSERT, UPDATE ON community_invite TO coupon_hub_bot_service;
