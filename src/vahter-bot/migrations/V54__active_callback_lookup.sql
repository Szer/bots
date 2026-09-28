CREATE TABLE active_callback (
    event_id BIGINT PRIMARY KEY,
    stream_id TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    action_channel_id BIGINT,
    action_message_id BIGINT,
    message_posted BOOLEAN NOT NULL
);

CREATE INDEX idx_active_callback_stream ON active_callback (stream_id);
CREATE INDEX idx_active_callback_age ON active_callback (created_at);
CREATE INDEX idx_active_callback_channel_age ON active_callback (action_channel_id, created_at);

CREATE VIEW active_callback_source AS
SELECT e.id::BIGINT AS event_id, e.stream_id, e.created_at,
       (e.data->>'actionChannelId')::BIGINT AS action_channel_id,
       p.action_message_id, COALESCE(p.message_posted, FALSE) AS message_posted
FROM event e
LEFT JOIN LATERAL (
    SELECT (posted.data->>'actionMessageId')::BIGINT AS action_message_id,
           TRUE AS message_posted
    FROM event posted
    WHERE posted.stream_id = e.stream_id AND posted.event_type = 'CallbackMessagePosted'
    LIMIT 1
) p ON TRUE
WHERE e.event_type = 'CallbackCreated'
  AND NOT EXISTS (
      SELECT 1 FROM event terminal
      WHERE terminal.stream_id = e.stream_id
        AND terminal.event_type IN ('CallbackResolved', 'CallbackExpired')
  );

CREATE FUNCTION refresh_active_callback(sid TEXT) RETURNS VOID LANGUAGE plpgsql AS $$
BEGIN
    DELETE FROM active_callback WHERE stream_id = sid;
    INSERT INTO active_callback
    SELECT * FROM active_callback_source WHERE stream_id = sid;
END $$;

CREATE FUNCTION project_active_callback() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'TRUNCATE' THEN
        TRUNCATE active_callback;
    ELSIF TG_OP = 'DELETE' THEN
        PERFORM refresh_active_callback(OLD.stream_id);
    ELSE
        IF TG_OP = 'UPDATE' AND OLD.stream_id <> NEW.stream_id THEN
            PERFORM refresh_active_callback(OLD.stream_id);
        END IF;
        PERFORM refresh_active_callback(NEW.stream_id);
    END IF;
    RETURN NULL;
END $$;

-- The trigger lock keeps event writes blocked until the backfill commits with the projection.
CREATE TRIGGER active_callback_insert AFTER INSERT ON event
FOR EACH ROW WHEN (NEW.event_type IN ('CallbackCreated', 'CallbackMessagePosted', 'CallbackResolved', 'CallbackExpired'))
EXECUTE FUNCTION project_active_callback();

CREATE TRIGGER active_callback_update AFTER UPDATE ON event
FOR EACH ROW WHEN (
    OLD.event_type IN ('CallbackCreated', 'CallbackMessagePosted', 'CallbackResolved', 'CallbackExpired')
    OR NEW.event_type IN ('CallbackCreated', 'CallbackMessagePosted', 'CallbackResolved', 'CallbackExpired')
)
EXECUTE FUNCTION project_active_callback();

CREATE TRIGGER active_callback_delete AFTER DELETE ON event
FOR EACH ROW WHEN (OLD.event_type IN ('CallbackCreated', 'CallbackMessagePosted', 'CallbackResolved', 'CallbackExpired'))
EXECUTE FUNCTION project_active_callback();

CREATE TRIGGER active_callback_truncate AFTER TRUNCATE ON event
FOR EACH STATEMENT EXECUTE FUNCTION project_active_callback();

INSERT INTO active_callback SELECT * FROM active_callback_source;

GRANT SELECT, INSERT, UPDATE, DELETE ON active_callback TO vahter_bot_ban_service;
GRANT SELECT ON active_callback_source TO vahter_bot_ban_service;
