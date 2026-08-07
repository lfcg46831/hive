ALTER TABLE inbox.items
    ADD COLUMN is_delegated boolean NOT NULL DEFAULT FALSE;

CREATE TABLE inbox.human_interactions (
    organization_id text NOT NULL,
    assigned_position_id text NOT NULL,
    message_id uuid NOT NULL,
    person_id text NOT NULL,
    read_state text NOT NULL CHECK (read_state IN ('Unread', 'Read')),
    reply_state text NOT NULL CHECK (reply_state IN ('NotStarted', 'InProgress')),
    draft_text text NULL,
    updated_at_utc timestamptz NOT NULL,
    PRIMARY KEY (
        organization_id,
        assigned_position_id,
        message_id,
        person_id),
    FOREIGN KEY (
        organization_id,
        assigned_position_id,
        message_id)
        REFERENCES inbox.items (
            organization_id,
            assigned_position_id,
            message_id)
        ON DELETE CASCADE,
    CHECK (
        char_length(person_id) BETWEEN 1 AND 256
        AND person_id = btrim(person_id)),
    CHECK (draft_text IS NULL OR reply_state = 'InProgress')
);

CREATE INDEX human_interactions_principal_idx
    ON inbox.human_interactions (organization_id, person_id);

CREATE TABLE inbox.human_interaction_audit (
    sequence bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    organization_id text NOT NULL,
    assigned_position_id text NOT NULL,
    message_id uuid NOT NULL,
    person_id text NOT NULL,
    action text NOT NULL CHECK (action IN (
        'MarkRead',
        'MarkUnread',
        'StartReply',
        'SaveDraft',
        'ClearDraft')),
    previous_read_state text NOT NULL CHECK (previous_read_state IN ('Unread', 'Read')),
    read_state text NOT NULL CHECK (read_state IN ('Unread', 'Read')),
    previous_reply_state text NOT NULL CHECK (
        previous_reply_state IN ('NotStarted', 'InProgress')),
    reply_state text NOT NULL CHECK (reply_state IN ('NotStarted', 'InProgress')),
    previous_draft_present boolean NOT NULL,
    draft_present boolean NOT NULL,
    occurred_at_utc timestamptz NOT NULL,
    CHECK (
        char_length(person_id) BETWEEN 1 AND 256
        AND person_id = btrim(person_id))
);

CREATE INDEX human_interaction_audit_item_idx
    ON inbox.human_interaction_audit (
        organization_id,
        assigned_position_id,
        message_id,
        person_id,
        sequence);
