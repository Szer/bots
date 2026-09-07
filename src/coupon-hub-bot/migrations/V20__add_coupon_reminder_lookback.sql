-- Seed the add-coupon reminder lookback so a fresh DB has a value (no env fallback in
-- buildBotConf) and the window is tunable without a deploy.
INSERT INTO bot_setting (key, value, type, feature_group, description) VALUES
    ('ADD_COUPON_REMINDER_LOOKBACK_DAYS', '2', 'FREE_FORM', 'REMINDER',
     'Days in a row the "Не забудь добавить купоны в бота" DM repeats after a used coupon; 0 or less disables it')
ON CONFLICT (key) DO NOTHING;
