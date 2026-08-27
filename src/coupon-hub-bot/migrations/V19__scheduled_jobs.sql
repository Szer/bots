-- Distributed scheduled-job locking (BotInfra.ScheduledJobs) — gates ReminderService's daily
-- run so exactly one pod (of 2+) sends each day's reminders, not one per pod.
CREATE TABLE scheduled_job (
    job_name          TEXT        PRIMARY KEY,
    last_completed_at TIMESTAMPTZ,
    locked_until      TIMESTAMPTZ,
    locked_by         TEXT
);

INSERT INTO scheduled_job (job_name) VALUES ('reminder_daily');

GRANT SELECT, UPDATE ON scheduled_job TO coupon_hub_bot_service;
