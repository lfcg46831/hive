CREATE TABLE organogram.position_state_projection_checkpoints (
    subscription text PRIMARY KEY
        CHECK (subscription IN ('PositionJournal', 'AuditLog')),
    source_offset bigint NOT NULL CHECK (source_offset >= 0),
    updated_at_utc timestamptz NOT NULL
);

INSERT INTO organogram.position_state_projection_checkpoints (
    subscription,
    source_offset,
    updated_at_utc)
VALUES
    ('PositionJournal', 0, CURRENT_TIMESTAMP),
    ('AuditLog', 0, CURRENT_TIMESTAMP);

CREATE TABLE organogram.position_state_projection_facts (
    sequence_id bigserial PRIMARY KEY,
    source text NOT NULL
        CHECK (source IN ('PositionEvent', 'OrganizationalMessage', 'AuditLog')),
    source_offset bigint NOT NULL CHECK (source_offset > 0),
    persistence_id text NULL,
    persistence_sequence bigint NULL CHECK (persistence_sequence IS NULL OR persistence_sequence > 0),
    organization_id text NOT NULL,
    position_id text NULL,
    fact_type text NOT NULL,
    message_id uuid NULL,
    thread_id uuid NULL,
    occurred_at_utc timestamptz NOT NULL,
    captured_at_utc timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    payload jsonb NOT NULL,
    UNIQUE (source, source_offset),
    CHECK (
        source = 'AuditLog'
        OR (
            persistence_id IS NOT NULL
            AND persistence_sequence IS NOT NULL
            AND position_id IS NOT NULL)),
    CHECK (
        source <> 'OrganizationalMessage'
        OR (message_id IS NOT NULL AND thread_id IS NOT NULL))
);

CREATE INDEX position_state_projection_facts_sequence_idx
    ON organogram.position_state_projection_facts (sequence_id);

CREATE INDEX position_state_projection_facts_position_idx
    ON organogram.position_state_projection_facts (
        organization_id,
        position_id,
        sequence_id);
