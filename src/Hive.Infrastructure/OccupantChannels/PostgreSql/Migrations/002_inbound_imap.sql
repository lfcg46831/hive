CREATE TABLE occupant_channel.imap_checkpoints (
    source_id text NOT NULL,
    mailbox text NOT NULL,
    uid_validity bigint NOT NULL,
    last_uid bigint NOT NULL,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (source_id, mailbox),
    CONSTRAINT ck_imap_checkpoint_uid_validity
        CHECK (uid_validity BETWEEN 1 AND 4294967295),
    CONSTRAINT ck_imap_checkpoint_last_uid
        CHECK (last_uid BETWEEN 0 AND 4294967295)
);

CREATE TABLE occupant_channel.imap_inbound_emails (
    source_id text NOT NULL,
    mailbox text NOT NULL,
    uid_validity bigint NOT NULL,
    uid bigint NOT NULL,
    raw_message bytea NOT NULL,
    captured_at timestamptz NOT NULL,
    processing_state text NOT NULL,
    PRIMARY KEY (source_id, mailbox, uid_validity, uid),
    CONSTRAINT ck_imap_inbound_uid_validity
        CHECK (uid_validity BETWEEN 1 AND 4294967295),
    CONSTRAINT ck_imap_inbound_uid
        CHECK (uid BETWEEN 1 AND 4294967295),
    CONSTRAINT ck_imap_inbound_raw_message
        CHECK (octet_length(raw_message) > 0),
    CONSTRAINT ck_imap_inbound_processing_state
        CHECK (processing_state IN ('pending', 'accepted', 'rejected'))
);

CREATE INDEX ix_imap_inbound_emails_pending
    ON occupant_channel.imap_inbound_emails (source_id, mailbox, captured_at, uid_validity, uid)
    WHERE processing_state = 'pending';
