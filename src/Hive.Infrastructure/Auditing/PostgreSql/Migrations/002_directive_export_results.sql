CREATE TABLE audit.directive_export_results (
    organization_id text NOT NULL,
    thread_id uuid NOT NULL,
    directive_id uuid NOT NULL,
    source_position_id text NOT NULL,
    message_type text NOT NULL,
    schema_version integer NOT NULL CHECK (schema_version > 0),
    content jsonb NOT NULL,
    captured_at_utc timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (organization_id, thread_id, directive_id)
);
