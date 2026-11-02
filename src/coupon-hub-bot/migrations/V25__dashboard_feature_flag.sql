INSERT INTO bot_setting (key, value, type, feature_group, description)
VALUES ('DASHBOARD_ENABLED', 'false', 'FEATURE_FLAG', 'DASHBOARD',
        'Enables the community dashboard for members; configured bot admins bypass this flag')
ON CONFLICT (key) DO NOTHING;
