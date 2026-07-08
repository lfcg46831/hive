CREATE TABLE audit.journey_events (
    sequence_id bigserial PRIMARY KEY,
    audit_event_id uuid NOT NULL UNIQUE,
    occurred_at_utc timestamptz NOT NULL,
    persisted_at_utc timestamptz NOT NULL,
    stage text NOT NULL,
    outcome text NOT NULL,
    reason_code text NULL,
    organization_id text NOT NULL,
    thread_id uuid NOT NULL,
    directive_id uuid NULL,
    message_id uuid NOT NULL,
    position_id text NULL,
    provider_id text NULL,
    model_id text NULL,
    message_type text NULL,
    latency_ms integer NULL CHECK (latency_ms IS NULL OR latency_ms >= 0),
    input_tokens integer NULL CHECK (input_tokens IS NULL OR input_tokens >= 0),
    output_tokens integer NULL CHECK (output_tokens IS NULL OR output_tokens >= 0),
    total_tokens integer NULL CHECK (total_tokens IS NULL OR total_tokens >= 0),
    tokens_estimated boolean NULL,
    cost_amount numeric(18, 6) NULL CHECK (cost_amount IS NULL OR cost_amount >= 0),
    cost_currency text NULL,
    cost_estimated boolean NULL,
    payload jsonb NOT NULL DEFAULT '{}'::jsonb
);

CREATE INDEX journey_events_thread_directive_sequence_idx
    ON audit.journey_events (thread_id, directive_id, sequence_id);

CREATE INDEX journey_events_organization_thread_sequence_idx
    ON audit.journey_events (organization_id, thread_id, sequence_id);
