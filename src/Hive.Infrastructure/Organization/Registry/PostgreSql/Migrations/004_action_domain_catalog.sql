ALTER TABLE registry.organizations
    ADD COLUMN action_domain_catalog jsonb NULL,
    ADD COLUMN action_domain_catalog_fingerprint text NULL,
    ADD COLUMN action_domain_catalog_updated_at timestamptz NULL;
