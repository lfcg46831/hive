CREATE TABLE occupant_channel.decision_token_uses (
    token_id uuid PRIMARY KEY,
    expires_at timestamptz NOT NULL,
    consumed_at timestamptz NOT NULL,
    CONSTRAINT ck_decision_token_use_window CHECK (expires_at > consumed_at)
);

CREATE INDEX ix_decision_token_uses_expires_at
    ON occupant_channel.decision_token_uses (expires_at);
