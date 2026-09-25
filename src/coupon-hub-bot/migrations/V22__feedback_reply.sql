CREATE TABLE feedback_delivery (
    feedback_id       BIGINT      NOT NULL REFERENCES user_feedback(id),
    admin_chat_id     BIGINT      NOT NULL,
    admin_message_id  BIGINT      NOT NULL,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (admin_chat_id, admin_message_id)
);

CREATE INDEX idx_feedback_delivery_feedback_id ON feedback_delivery(feedback_id);

CREATE TABLE feedback_reply (
    id           BIGSERIAL   PRIMARY KEY,
    feedback_id  BIGINT      NOT NULL REFERENCES user_feedback(id),
    admin_id     BIGINT      NOT NULL,
    reply_text   TEXT        NOT NULL,
    delivered    BOOLEAN     NOT NULL,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX idx_feedback_reply_feedback_id ON feedback_reply(feedback_id);

GRANT SELECT, INSERT ON feedback_delivery TO coupon_hub_bot_service;
GRANT SELECT, INSERT ON feedback_reply TO coupon_hub_bot_service;
GRANT USAGE, SELECT ON SEQUENCE feedback_reply_id_seq TO coupon_hub_bot_service;
