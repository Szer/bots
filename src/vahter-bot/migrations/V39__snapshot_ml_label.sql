-- ML training label for snapshot_message: the latest DECISIVE spam/ham verdict across BOTH the
-- message stream (MessageMarkedSpam -> spam, MessageMarkedHam -> ham) and the moderation stream
-- (BotAutoDeleted + VahterActed PotentialKill/ManualBan -> spam; PotentialNotSpam/DetectedNotSpam
-- -> ham). "Latest event wins" by the deciding event's timestamp, matching the verdict logic in
-- DbService.MlData. Reaction-triage / soft-spam actions are non-decisive and ignored, as in MlData.
--
-- Maintained by the upserts in DB.fs (NOT a GENERATED column): the two streams are written in
-- separate transactions, so the cross-stream "latest wins" comparison needs the deciding event's
-- time stored (ml_label_at) and a keep-newer guard on write.
ALTER TABLE snapshot_message
    ADD COLUMN IF NOT EXISTS ml_label    TEXT,         -- 'spam' | 'ham' | NULL (unknown / unlabeled)
    ADD COLUMN IF NOT EXISTS ml_label_at TIMESTAMPTZ;  -- deciding event's created_at (cross-stream tie-break)

CREATE INDEX IF NOT EXISTS idx_snapshot_message_ml_label
    ON snapshot_message (ml_label) WHERE ml_label IS NOT NULL;

-- Existing rows keep ml_label = NULL until POST /rebuild-snapshots is re-run (idempotent); the
-- rebuild recomputes the label for every stream. Table-level grants already cover the new columns.
