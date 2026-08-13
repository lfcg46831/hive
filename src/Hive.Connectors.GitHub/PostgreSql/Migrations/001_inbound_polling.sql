CREATE TABLE github_connector.polling_checkpoints (
    instance_id text NOT NULL,
    repository text NOT NULL,
    cursor text NULL,
    not_before timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (instance_id, repository),
    CONSTRAINT polling_checkpoint_instance_nonempty CHECK (length(instance_id) > 0),
    CONSTRAINT polling_checkpoint_repository_nonempty CHECK (length(repository) > 0)
);

CREATE TABLE github_connector.inbound_events (
    instance_id text NOT NULL,
    repository text NOT NULL,
    external_event_id text NOT NULL,
    event_kind text NOT NULL,
    payload jsonb NOT NULL,
    captured_at timestamptz NOT NULL,
    processing_state text NOT NULL,
    PRIMARY KEY (instance_id, repository, external_event_id),
    CONSTRAINT inbound_event_kind_known CHECK (event_kind IN ('issue', 'comment')),
    CONSTRAINT inbound_event_state_known CHECK (processing_state IN ('pending')),
    CONSTRAINT inbound_event_external_id_nonempty CHECK (length(external_event_id) > 0)
);

CREATE INDEX inbound_events_pending_idx
    ON github_connector.inbound_events (instance_id, repository, captured_at, external_event_id)
    WHERE processing_state = 'pending';
