ALTER TABLE occupant_channel.imap_inbound_emails
    ADD COLUMN reply_emission_state text,
    ADD COLUMN reply_message_id uuid,
    ADD COLUMN reply_directive_id uuid,
    ADD COLUMN reply_emission_at timestamptz,
    ADD COLUMN reply_emission_failure_codes text[],
    ADD CONSTRAINT ck_imap_inbound_work_reply_emission CHECK (
        (
            processing_state <> 'accepted'
            AND reply_emission_state IS NULL
            AND reply_message_id IS NULL
            AND reply_directive_id IS NULL
            AND reply_emission_at IS NULL
            AND reply_emission_failure_codes IS NULL
        )
        OR
        (
            processing_state = 'accepted'
            AND request_id IS NOT NULL
            AND reply_emission_state IS NULL
            AND reply_message_id IS NULL
            AND reply_directive_id IS NULL
            AND reply_emission_at IS NULL
            AND reply_emission_failure_codes IS NULL
        )
        OR
        (
            processing_state = 'accepted'
            AND request_id IS NULL
            AND reply_emission_state IS NOT NULL
            AND (
                (
                    reply_emission_state = 'pending'
                    AND reply_message_id IS NULL
                    AND reply_directive_id IS NULL
                    AND reply_emission_at IS NULL
                    AND reply_emission_failure_codes IS NULL
                )
                OR
                (
                    reply_emission_state = 'emitted'
                    AND reply_message_id IS NOT NULL
                    AND reply_directive_id IS NOT NULL
                    AND reply_emission_at IS NOT NULL
                    AND reply_emission_failure_codes IS NULL
                )
                OR
                (
                    reply_emission_state = 'rejected'
                    AND reply_message_id IS NOT NULL
                    AND reply_directive_id IS NOT NULL
                    AND reply_emission_at IS NOT NULL
                    AND reply_emission_failure_codes IS NOT NULL
                    AND cardinality(reply_emission_failure_codes) > 0
                )
            )
        )
    );

UPDATE occupant_channel.imap_inbound_emails
SET reply_emission_state = 'pending'
WHERE processing_state = 'accepted'
  AND request_id IS NULL;

CREATE INDEX ix_imap_inbound_emails_reply_emission_pending
    ON occupant_channel.imap_inbound_emails (
        source_id,
        mailbox,
        processed_at,
        uid_validity,
        uid)
    WHERE processing_state = 'accepted'
      AND request_id IS NULL
      AND reply_emission_state = 'pending';
