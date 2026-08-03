CREATE TABLE organogram.position_states (
    organization_id text NOT NULL,
    position_id text NOT NULL,
    state text NOT NULL CHECK (state IN ('Offline', 'Blocked', 'WaitingHuman', 'Working', 'Idle')),
    sequence bigint NOT NULL CHECK (sequence >= 0),
    updated_at_utc timestamptz NOT NULL,
    last_event_type text NULL,
    last_event_thread_id uuid NULL,
    last_event_occurred_at_utc timestamptz NULL,
    PRIMARY KEY (organization_id, position_id),
    CHECK (
        (last_event_type IS NULL
            AND last_event_thread_id IS NULL
            AND last_event_occurred_at_utc IS NULL)
        OR
        (last_event_type IS NOT NULL
            AND last_event_thread_id IS NOT NULL
            AND last_event_occurred_at_utc IS NOT NULL))
);

INSERT INTO organogram.position_states (
    organization_id,
    position_id,
    state,
    sequence,
    updated_at_utc)
SELECT position.organization_id,
       position.position_id,
       'Idle',
       0,
       snapshot.imported_at_utc
FROM organogram.current_snapshots current
INNER JOIN organogram.snapshots snapshot
    ON snapshot.organization_id = current.organization_id
   AND snapshot.registry_version = current.registry_version
INNER JOIN organogram.positions position
    ON position.organization_id = current.organization_id
   AND position.registry_version = current.registry_version;
