CREATE TABLE evaluation.result_projections (
    organization_id text NOT NULL,
    thread_id uuid NOT NULL,
    directive_id uuid NOT NULL,
    message_id uuid NOT NULL,
    projection_version integer NOT NULL CHECK (projection_version > 0),
    severity text NULL CHECK (severity IS NULL OR severity IN ('low', 'medium', 'high', 'critical')),
    missing_information text[] NULL,
    PRIMARY KEY (organization_id, thread_id, directive_id),
    UNIQUE (organization_id, message_id)
);

CREATE INDEX result_projections_thread_directive_idx
    ON evaluation.result_projections (thread_id, directive_id);
