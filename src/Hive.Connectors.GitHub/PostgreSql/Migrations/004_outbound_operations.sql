CREATE TABLE github_connector.outbound_operations (
    operation_key char(64) PRIMARY KEY,
    payload_hash char(64) NOT NULL,
    instance_id text NOT NULL,
    organization_id text NOT NULL,
    repository text NOT NULL,
    issue_number bigint NOT NULL CHECK (issue_number > 0),
    thread_id uuid NOT NULL,
    directive_id uuid NOT NULL,
    position_id text NOT NULL,
    tool_name text NOT NULL,
    operation_state text NOT NULL CHECK (operation_state IN ('pending', 'succeeded', 'rejected')),
    attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    last_code text NULL,
    receipt text NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    completed_at timestamptz NULL,
    CHECK (
        (operation_state = 'pending' AND receipt IS NULL AND completed_at IS NULL)
        OR (operation_state = 'succeeded' AND last_code IS NULL AND receipt IS NOT NULL AND completed_at IS NOT NULL)
        OR (operation_state = 'rejected' AND last_code IS NOT NULL AND receipt IS NULL AND completed_at IS NOT NULL)
    )
);

CREATE INDEX ix_github_outbound_operations_correlation
    ON github_connector.outbound_operations (organization_id, thread_id, directive_id);
