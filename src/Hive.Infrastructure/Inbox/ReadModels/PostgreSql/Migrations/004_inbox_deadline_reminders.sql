ALTER TABLE inbox.items
    ADD COLUMN last_reminder_at_utc timestamptz NULL,
    ADD CONSTRAINT items_deadline_reminder_window CHECK (
        last_reminder_at_utc IS NULL
        OR (
            deadline_at_utc IS NOT NULL
            AND last_reminder_at_utc >= sent_at_utc
            AND last_reminder_at_utc < deadline_at_utc));
