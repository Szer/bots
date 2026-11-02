CREATE TABLE dashboard_session (
    token_hash bytea PRIMARY KEY,
    user_id bigint NOT NULL,
    community_id bigint NOT NULL,
    expires_at timestamptz NOT NULL
);

CREATE INDEX dashboard_session_expiry_idx ON dashboard_session (expires_at);
GRANT SELECT, INSERT, DELETE ON dashboard_session TO coupon_hub_bot_service;

INSERT INTO bot_setting (key, value, type, feature_group)
VALUES ('DASHBOARD_URL', 'https://coupon.szer.dev/dashboard', 'FREE_FORM', 'DASHBOARD')
ON CONFLICT (key) DO NOTHING;
