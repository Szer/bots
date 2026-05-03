-- Partial index on event.msg_text_md5 for MessageReceived events.
-- Powers the per-message repeat-count lookup used by the ML inference path
-- (DbService.GetTextRepeatCount) and by the training-set SQL's
-- text_repeat_counts CTE. Without this index those become sequential scans
-- over the full event log (~700k+ rows).
--
-- Partial WHERE matches the access pattern: we only ever count
-- MessageReceived rows with a non-null hash. msg_text_md5 is NULL for
-- events whose data has no text field (most non-MessageReceived events).

CREATE INDEX IF NOT EXISTS idx_event_msg_text_md5
    ON event(msg_text_md5)
    WHERE event_type = 'MessageReceived' AND msg_text_md5 IS NOT NULL;
