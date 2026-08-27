-- Postgres-backed chat-admin snapshot, shared across pods (UpdateChatAdmins.fs). One pod fetches
-- GetChatAdministrators under the 'chat_admins_refresh' scheduled_job lease and replaces this
-- table; every pod reloads its local snapshot (UpdateChatAdmins.Admins) from here on a timer.
CREATE TABLE chat_admin (
    chat_id    BIGINT      NOT NULL,
    user_id    BIGINT      NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (chat_id, user_id)
);

GRANT SELECT, INSERT, UPDATE, DELETE ON chat_admin TO vahter_bot_ban_service;

-- Same lease table/mechanism as V19's daily jobs, mirrored to seed the new job row.
INSERT INTO scheduled_job (job_name) VALUES ('chat_admins_refresh');
