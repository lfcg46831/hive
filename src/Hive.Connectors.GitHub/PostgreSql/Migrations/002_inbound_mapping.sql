ALTER TABLE github_connector.inbound_events
    DROP CONSTRAINT inbound_event_state_known;

ALTER TABLE github_connector.inbound_events
    ADD COLUMN processed_at timestamptz NULL,
    ADD COLUMN rejection_code text NULL,
    ADD CONSTRAINT inbound_event_state_known
        CHECK (processing_state IN ('pending', 'submitted', 'rejected')),
    ADD CONSTRAINT inbound_event_completion_consistent CHECK (
        (processing_state = 'pending' AND processed_at IS NULL AND rejection_code IS NULL)
        OR (processing_state = 'submitted' AND processed_at IS NOT NULL AND rejection_code IS NULL)
        OR (processing_state = 'rejected' AND processed_at IS NOT NULL
            AND rejection_code IS NOT NULL AND length(rejection_code) > 0)
    );
