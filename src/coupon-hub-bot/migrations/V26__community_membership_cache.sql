CREATE TABLE community_membership_cache (
    chat_id bigint NOT NULL,
    user_id bigint NOT NULL,
    revision uuid NOT NULL DEFAULT gen_random_uuid(),
    is_member boolean,
    checked_at timestamptz NOT NULL,
    PRIMARY KEY (chat_id, user_id)
);

GRANT SELECT, INSERT, UPDATE, DELETE ON community_membership_cache TO coupon_hub_bot_service;
