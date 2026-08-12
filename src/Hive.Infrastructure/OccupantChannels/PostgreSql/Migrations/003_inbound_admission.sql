ALTER TABLE occupant_channel.decision_token_uses
    ADD COLUMN operation_id uuid;

UPDATE occupant_channel.decision_token_uses
SET operation_id = token_id
WHERE operation_id IS NULL;

ALTER TABLE occupant_channel.decision_token_uses
    ALTER COLUMN operation_id SET NOT NULL;

ALTER TABLE occupant_channel.imap_inbound_emails
    ADD COLUMN processed_at timestamptz,
    ADD COLUMN failure_code text,
    ADD COLUMN token_id uuid,
    ADD COLUMN token_issued_at timestamptz,
    ADD COLUMN token_expires_at timestamptz,
    ADD COLUMN organization_id text,
    ADD COLUMN position_id text,
    ADD COLUMN message_id uuid,
    ADD COLUMN thread_id uuid,
    ADD COLUMN request_id uuid,
    ADD COLUMN occupant_id text,
    ADD COLUMN user_id uuid,
    ADD COLUMN binding_id uuid,
    ADD COLUMN reply_text text,
    ADD COLUMN content_trust text,
    ADD CONSTRAINT ck_imap_inbound_admission_result CHECK (
        (
            processing_state = 'pending'
            AND processed_at IS NULL
            AND failure_code IS NULL
            AND token_id IS NULL
            AND token_issued_at IS NULL
            AND token_expires_at IS NULL
            AND organization_id IS NULL
            AND position_id IS NULL
            AND message_id IS NULL
            AND thread_id IS NULL
            AND request_id IS NULL
            AND occupant_id IS NULL
            AND user_id IS NULL
            AND binding_id IS NULL
            AND reply_text IS NULL
            AND content_trust IS NULL
        )
        OR
        (
            processing_state = 'accepted'
            AND processed_at IS NOT NULL
            AND failure_code IS NULL
            AND token_id IS NOT NULL
            AND token_issued_at IS NOT NULL
            AND token_expires_at IS NOT NULL
            AND token_expires_at > token_issued_at
            AND organization_id IS NOT NULL
            AND position_id IS NOT NULL
            AND message_id IS NOT NULL
            AND thread_id IS NOT NULL
            AND occupant_id IS NOT NULL
            AND user_id IS NOT NULL
            AND binding_id IS NOT NULL
            AND reply_text IS NOT NULL
            AND length(btrim(reply_text)) > 0
            AND content_trust = 'untrusted'
        )
        OR
        (
            processing_state = 'rejected'
            AND processed_at IS NOT NULL
            AND failure_code IN (
                'malformed-message',
                'sender-missing',
                'sender-ambiguous',
                'plain-text-body-missing',
                'correlation-token-missing',
                'correlation-token-ambiguous',
                'plain-text-reply-missing',
                'token-malformed',
                'token-unsupported-version',
                'token-invalid-signature',
                'token-not-yet-valid',
                'token-expired',
                'occupation-missing',
                'occupation-revoked',
                'binding-missing',
                'binding-revoked',
                'identity-ambiguous',
                'sender-mismatch',
                'decision-token-already-used'
            )
            AND token_id IS NULL
            AND token_issued_at IS NULL
            AND token_expires_at IS NULL
            AND organization_id IS NULL
            AND position_id IS NULL
            AND message_id IS NULL
            AND thread_id IS NULL
            AND request_id IS NULL
            AND occupant_id IS NULL
            AND user_id IS NULL
            AND binding_id IS NULL
            AND reply_text IS NULL
            AND content_trust IS NULL
        )
    );

CREATE INDEX ix_imap_inbound_emails_accepted
    ON occupant_channel.imap_inbound_emails (
        source_id,
        mailbox,
        processed_at,
        uid_validity,
        uid)
    WHERE processing_state = 'accepted';
