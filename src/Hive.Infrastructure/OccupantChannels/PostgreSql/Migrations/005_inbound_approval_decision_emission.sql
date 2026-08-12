ALTER TABLE occupant_channel.imap_inbound_emails
    ADD COLUMN decision_emission_state text,
    ADD COLUMN decision_message_id uuid,
    ADD COLUMN decision_emission_at timestamptz,
    ADD COLUMN decision_emission_failure_codes text[];

UPDATE occupant_channel.imap_inbound_emails
SET decision_emission_state = 'pending'
WHERE processing_state = 'accepted'
  AND request_id IS NOT NULL;

ALTER TABLE occupant_channel.imap_inbound_emails
    ADD CONSTRAINT ck_imap_inbound_approval_decision_emission CHECK (
        (
            processing_state <> 'accepted'
            AND decision_emission_state IS NULL
            AND decision_message_id IS NULL
            AND decision_emission_at IS NULL
            AND decision_emission_failure_codes IS NULL
        )
        OR
        (
            processing_state = 'accepted'
            AND request_id IS NULL
            AND decision_emission_state IS NULL
            AND decision_message_id IS NULL
            AND decision_emission_at IS NULL
            AND decision_emission_failure_codes IS NULL
        )
        OR
        (
            processing_state = 'accepted'
            AND request_id IS NOT NULL
            AND decision_emission_state IS NOT NULL
            AND (
                (
                    decision_emission_state = 'pending'
                    AND decision_message_id IS NULL
                    AND decision_emission_at IS NULL
                    AND decision_emission_failure_codes IS NULL
                )
                OR
                (
                    decision_emission_state = 'emitted'
                    AND decision_message_id IS NOT NULL
                    AND decision_emission_at IS NOT NULL
                    AND decision_emission_failure_codes IS NULL
                )
                OR
                (
                    decision_emission_state = 'rejected'
                    AND decision_message_id IS NOT NULL
                    AND decision_emission_at IS NOT NULL
                    AND decision_emission_failure_codes IS NOT NULL
                    AND cardinality(decision_emission_failure_codes) > 0
                )
            )
        )
    );

CREATE INDEX ix_imap_inbound_emails_decision_emission_pending
    ON occupant_channel.imap_inbound_emails (
        source_id,
        mailbox,
        processed_at,
        uid_validity,
        uid)
    WHERE processing_state = 'accepted'
      AND request_id IS NOT NULL
      AND decision_emission_state = 'pending';
