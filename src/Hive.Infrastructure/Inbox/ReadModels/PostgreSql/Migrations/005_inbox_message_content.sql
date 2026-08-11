ALTER TABLE inbox.items
    ADD COLUMN message_content jsonb NULL;

WITH message_facts AS (
    SELECT DISTINCT ON (organization_id, position_id, message_id)
           organization_id,
           position_id,
           message_id,
           payload
    FROM inbox.projection_facts
    WHERE source = 'OrganizationalMessage'
    ORDER BY organization_id, position_id, message_id, sequence_id DESC
)
UPDATE inbox.items AS item
SET message_content = CASE item.message_type
    WHEN 'Directive' THEN jsonb_build_object(
        'objective', fact.payload -> 'Objective',
        'context', fact.payload -> 'Context')
    WHEN 'Report' THEN jsonb_build_object(
        'body', fact.payload -> 'Body',
        'kind', fact.payload -> 'Kind')
    WHEN 'Escalation' THEN jsonb_build_object(
        'issue', fact.payload -> 'Issue',
        'context', fact.payload -> 'Context')
    WHEN 'Memo' THEN jsonb_build_object(
        'body', fact.payload -> 'Body')
    WHEN 'PeerRequest' THEN jsonb_build_object(
        'ask', fact.payload -> 'Ask')
    WHEN 'PeerResponse' THEN jsonb_build_object(
        'body', fact.payload -> 'Body')
    WHEN 'ApprovalRequest' THEN jsonb_build_object(
        'action', fact.payload -> 'Action',
        'justification', fact.payload -> 'Justification')
    WHEN 'ApprovalDecision' THEN
        CASE
            WHEN fact.payload -> 'Reason' IS NULL
                OR fact.payload -> 'Reason' = 'null'::jsonb
                THEN '{}'::jsonb
            ELSE jsonb_build_object('reason', fact.payload -> 'Reason')
        END
END
FROM message_facts AS fact
WHERE fact.organization_id = item.organization_id
  AND fact.position_id = item.assigned_position_id
  AND fact.message_id = item.message_id;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM inbox.items WHERE message_content IS NULL) THEN
        RAISE EXCEPTION
            'Inbox message content could not be backfilled from the durable projection facts.';
    END IF;
END;
$$;

ALTER TABLE inbox.items
    ALTER COLUMN message_content SET NOT NULL,
    ADD CONSTRAINT items_message_content_shape CHECK (
        jsonb_typeof(message_content) = 'object'
        AND CASE message_type
            WHEN 'Directive' THEN
                message_content ?& ARRAY['objective', 'context']
                AND message_content - 'objective' - 'context' = '{}'::jsonb
                AND jsonb_typeof(message_content -> 'objective') = 'string'
                AND jsonb_typeof(message_content -> 'context') = 'string'
            WHEN 'Report' THEN
                message_content ?& ARRAY['body', 'kind']
                AND message_content - 'body' - 'kind' = '{}'::jsonb
                AND jsonb_typeof(message_content -> 'body') = 'string'
                AND message_content ->> 'kind' IN ('progress', 'done')
            WHEN 'Escalation' THEN
                message_content ?& ARRAY['issue', 'context']
                AND message_content - 'issue' - 'context' = '{}'::jsonb
                AND jsonb_typeof(message_content -> 'issue') = 'string'
                AND jsonb_typeof(message_content -> 'context') = 'string'
            WHEN 'Memo' THEN
                message_content ? 'body'
                AND message_content - 'body' = '{}'::jsonb
                AND jsonb_typeof(message_content -> 'body') = 'string'
            WHEN 'PeerRequest' THEN
                message_content ? 'ask'
                AND message_content - 'ask' = '{}'::jsonb
                AND jsonb_typeof(message_content -> 'ask') = 'string'
            WHEN 'PeerResponse' THEN
                message_content ? 'body'
                AND message_content - 'body' = '{}'::jsonb
                AND jsonb_typeof(message_content -> 'body') = 'string'
            WHEN 'ApprovalRequest' THEN
                message_content ?& ARRAY['action', 'justification']
                AND message_content - 'action' - 'justification' = '{}'::jsonb
                AND jsonb_typeof(message_content -> 'action') = 'string'
                AND jsonb_typeof(message_content -> 'justification') = 'string'
                AND message_content ->> 'action' = approval_action
            WHEN 'ApprovalDecision' THEN
                message_content = '{}'::jsonb
                OR (
                    message_content ? 'reason'
                    AND message_content - 'reason' = '{}'::jsonb
                    AND jsonb_typeof(message_content -> 'reason') = 'string')
            ELSE FALSE
        END);
