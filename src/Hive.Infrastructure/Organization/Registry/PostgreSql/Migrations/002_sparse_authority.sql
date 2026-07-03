ALTER TABLE registry.authorities
    ADD COLUMN overrides jsonb NOT NULL DEFAULT '[]'::jsonb;

ALTER TABLE registry.authorities
    DROP COLUMN must_escalate,
    DROP COLUMN requires_human_approval;

ALTER TABLE registry.authorities
    ALTER COLUMN overrides DROP DEFAULT;
