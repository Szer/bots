-- user_id on "<type>_reverted" rows is now the ADMIN who ran /undo; subject_user_id is the
-- user whose action was cancelled. NULL on every other event_type.
ALTER TABLE coupon_event
    ADD COLUMN IF NOT EXISTS subject_user_id BIGINT NULL REFERENCES "user" (id) ON DELETE SET NULL;

-- Backfill: old "_reverted" rows still carry the original actor in user_id (admin unknown),
-- so subject = user_id nets identically to before this migration.
UPDATE coupon_event
SET subject_user_id = user_id
WHERE event_type LIKE '%\_reverted'
  AND subject_user_id IS NULL;
