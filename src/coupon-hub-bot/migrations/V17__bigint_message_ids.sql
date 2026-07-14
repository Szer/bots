-- Widen all Telegram message-id columns from INT to BIGINT.
-- Telegram message ids are 64-bit (Bot API); the upcoming Funogram migration
-- carries them as int64 end-to-end, and the app code now uses int64 too.

ALTER TABLE chat_message           ALTER COLUMN message_id          TYPE BIGINT;
ALTER TABLE chat_message           ALTER COLUMN reply_to_message_id TYPE BIGINT;
ALTER TABLE user_feedback          ALTER COLUMN telegram_message_id TYPE BIGINT;
ALTER TABLE pending_add_batch      ALTER COLUMN bulk_message_id     TYPE BIGINT;
ALTER TABLE pending_add_batch_item ALTER COLUMN photo_message_id    TYPE BIGINT;
