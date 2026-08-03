CREATE TABLE organogram.position_state_projection_progress (
    projection text PRIMARY KEY CHECK (projection = 'LiveState'),
    sequence_id bigint NOT NULL CHECK (sequence_id >= 0),
    last_event_applied_at_utc timestamptz NULL,
    updated_at_utc timestamptz NOT NULL,
    CHECK (
        (sequence_id = 0 AND last_event_applied_at_utc IS NULL)
        OR
        (sequence_id > 0 AND last_event_applied_at_utc IS NOT NULL))
);

INSERT INTO organogram.position_state_projection_progress (
    projection,
    sequence_id,
    last_event_applied_at_utc,
    updated_at_utc)
VALUES ('LiveState', 0, NULL, CURRENT_TIMESTAMP);

CREATE TABLE organogram.position_state_projection_watermarks (
    organization_id text PRIMARY KEY,
    sequence_id bigint NOT NULL CHECK (sequence_id > 0),
    last_event_applied_at_utc timestamptz NOT NULL,
    updated_at_utc timestamptz NOT NULL
);
