CREATE TABLE inbox.projection_progress (
    projection text PRIMARY KEY CHECK (projection = 'Inbox'),
    sequence_id bigint NOT NULL CHECK (sequence_id >= 0),
    last_event_applied_at_utc timestamptz NULL,
    updated_at_utc timestamptz NOT NULL,
    CHECK (
        (sequence_id = 0 AND last_event_applied_at_utc IS NULL)
        OR
        (sequence_id > 0 AND last_event_applied_at_utc IS NOT NULL))
);

INSERT INTO inbox.projection_progress (
    projection,
    sequence_id,
    last_event_applied_at_utc,
    updated_at_utc)
VALUES ('Inbox', 0, NULL, CURRENT_TIMESTAMP);

CREATE TABLE inbox.projection_watermarks (
    organization_id text PRIMARY KEY,
    sequence_id bigint NOT NULL CHECK (sequence_id > 0),
    last_event_applied_at_utc timestamptz NOT NULL,
    updated_at_utc timestamptz NOT NULL
);

CREATE TABLE inbox.items (
    organization_id text NOT NULL,
    assigned_position_id text NOT NULL,
    message_id uuid NOT NULL,
    message_type text NOT NULL CHECK (message_type IN (
        'Directive',
        'Report',
        'Escalation',
        'Memo',
        'PeerRequest',
        'PeerResponse',
        'ApprovalRequest',
        'ApprovalDecision')),
    origin_type text NOT NULL CHECK (origin_type IN ('Position', 'OrganizationOwner')),
    origin_position_id text NULL,
    destination_type text NOT NULL CHECK (destination_type IN ('Position', 'OrganizationOwner')),
    destination_position_id text NULL,
    thread_id uuid NOT NULL,
    priority text NOT NULL CHECK (priority IN ('Low', 'Normal', 'High', 'Critical')),
    sent_at_utc timestamptz NOT NULL,
    deadline_at_utc timestamptz NULL,
    is_expired boolean NOT NULL,
    response_state text NOT NULL CHECK (response_state IN (
        'NotApplicable',
        'AwaitingResponse',
        'Responded')),
    approval_request_id uuid NULL,
    approval_action text NULL,
    approval_policy_ref text NULL,
    approval_state text NULL CHECK (approval_state IS NULL OR approval_state IN (
        'Pending',
        'Approved',
        'Rejected',
        'Expired')),
    approval_decision_message_id uuid NULL,
    approval_decided_at_utc timestamptz NULL,
    last_fact_type text NOT NULL,
    last_changed_at_utc timestamptz NOT NULL,
    PRIMARY KEY (organization_id, assigned_position_id, message_id),
    CHECK (
        (origin_type = 'Position' AND origin_position_id IS NOT NULL)
        OR
        (origin_type = 'OrganizationOwner' AND origin_position_id IS NULL)),
    CHECK (
        (destination_type = 'Position' AND destination_position_id IS NOT NULL)
        OR
        (destination_type = 'OrganizationOwner' AND destination_position_id IS NULL)),
    CHECK (deadline_at_utc IS NULL OR deadline_at_utc >= sent_at_utc),
    CHECK (
        (message_type NOT IN ('ApprovalRequest', 'ApprovalDecision')
            AND approval_request_id IS NULL
            AND approval_action IS NULL
            AND approval_policy_ref IS NULL
            AND approval_state IS NULL
            AND approval_decision_message_id IS NULL
            AND approval_decided_at_utc IS NULL)
        OR
        (message_type IN ('ApprovalRequest', 'ApprovalDecision')
            AND approval_request_id IS NOT NULL
            AND approval_action IS NOT NULL
            AND approval_policy_ref IS NOT NULL
            AND approval_state IS NOT NULL)),
    CHECK (
        (approval_state IN ('Approved', 'Rejected')
            AND approval_decision_message_id IS NOT NULL
            AND approval_decided_at_utc IS NOT NULL)
        OR
        (approval_state IS NULL OR approval_state IN ('Pending', 'Expired'))
            AND approval_decision_message_id IS NULL
            AND approval_decided_at_utc IS NULL)
);

CREATE INDEX items_position_order_idx
    ON inbox.items (
        organization_id,
        assigned_position_id,
        deadline_at_utc,
        priority,
        sent_at_utc,
        message_id);

CREATE INDEX items_thread_idx
    ON inbox.items (organization_id, thread_id);
