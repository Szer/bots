-- Local-dev seed for bot_setting, copied from the production coupon-bot DB
-- (snapshot 2026-05-23). Runs once via the `seed-bot-settings` compose service
-- AFTER Flyway has created the table in V15.
--
-- ON CONFLICT DO NOTHING so manual UPDATEs during a test session are not
-- clobbered on the next `docker compose up`. To force a refresh:
--   docker exec -it coupon-hub-postgres-dev psql -U admin -d coupon_hub_bot \
--     -c "DELETE FROM bot_setting;"
--   docker compose -f src/coupon-hub-bot/docker-compose.dev.yml run --rm seed-bot-settings
--
-- Note: secrets (tokens, OCR key) still come from .env.local. AZURE_OCR_ENDPOINT
-- below is the prod endpoint; OCR calls will only succeed if AZURE_OCR_KEY in
-- .env.local is the matching key (or you point the endpoint at your own Azure
-- resource).

INSERT INTO bot_setting (key, value, type, feature_group, description) VALUES
    ('MAX_TAKEN_COUPONS',      '6',                                                    'FREE_FORM',    'coupons',     'Max coupons a single user can hold at once'),
    ('TEST_MODE',              'false',                                                'FEATURE_FLAG', 'diagnostics', 'Enables test-only endpoints (e.g. /test/run-reminder)'),
    ('FEEDBACK_ADMINS',        '432506904;509847134',                                  'FREE_FORM',    'feedback',    'Telegram user ids (; , or space separated) who receive /feedback reports'),
    ('GITHUB_REPO',            'Szer/coupon-bot',                                      'FREE_FORM',    'feedback',    'owner/repo used by /feedback to open GitHub issues'),
    ('AZURE_OCR_ENDPOINT',     'https://szer-vision-ocr.cognitiveservices.azure.com', 'FREE_FORM',    'ocr',         'Azure Computer Vision resource base URL'),
    ('OCR_ENABLED',            'true',                                                 'FEATURE_FLAG', 'ocr',         'Master toggle for Azure OCR pre-fill of coupon fields'),
    ('OCR_MAX_FILE_SIZE_BYTES','20971520',                                             'FREE_FORM',    'ocr',         'Maximum photo size in bytes that will be sent to Azure OCR'),
    ('REMINDER_HOUR_DUBLIN',   '10',                                                   'FREE_FORM',    'reminders',   'Hour of day (Europe/Dublin) to send daily reminders'),
    ('REMINDER_RUN_ON_START',  'false',                                                'FEATURE_FLAG', 'reminders',   'Run the reminder job immediately on startup (debug only)'),
    ('COMMUNITY_CHAT_ID',      '-5252116059',                                          'FREE_FORM',    'telegram',    'Telegram group chat id the bot serves')
ON CONFLICT (key) DO NOTHING;
