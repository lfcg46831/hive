CREATE TABLE scheduler.pulse_deliveries (
    idempotency_key text PRIMARY KEY,
    organization_id text NOT NULL,
    position_id text NOT NULL,
    schedule_id text NOT NULL,
    window_start timestamptz NOT NULL,
    window_end timestamptz NOT NULL,
    message_id uuid NOT NULL,
    thread_id uuid NOT NULL,
    status text NOT NULL,
    attempt_count integer NOT NULL,
    last_occurred_at timestamptz NOT NULL,
    reason_code text NULL,
    reason_message text NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    CONSTRAINT ck_pulse_deliveries_status CHECK (
        status IN ('Registered', 'Fired', 'Delivered', 'Skipped', 'Failed', 'Redelivered')),
    CONSTRAINT ck_pulse_deliveries_attempt_count CHECK (attempt_count >= 1),
    CONSTRAINT ck_pulse_deliveries_window CHECK (window_end > window_start)
);

CREATE TABLE scheduler.pulse_delivery_history (
    idempotency_key text NOT NULL REFERENCES scheduler.pulse_deliveries(idempotency_key) ON DELETE CASCADE,
    sequence integer NOT NULL,
    status text NOT NULL,
    occurred_at timestamptz NOT NULL,
    reason_code text NULL,
    reason_message text NULL,
    PRIMARY KEY (idempotency_key, sequence),
    CONSTRAINT ck_pulse_delivery_history_status CHECK (
        status IN ('Registered', 'Fired', 'Delivered', 'Skipped', 'Failed', 'Redelivered')),
    CONSTRAINT ck_pulse_delivery_history_sequence CHECK (sequence >= 1)
);

CREATE INDEX ix_pulse_deliveries_identity
ON scheduler.pulse_deliveries (organization_id, position_id, schedule_id, window_start);
