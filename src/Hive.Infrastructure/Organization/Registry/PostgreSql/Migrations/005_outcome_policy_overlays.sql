ALTER TABLE registry.organizations
    ADD COLUMN outcome_policy jsonb NULL;

ALTER TABLE registry.occupants
    ADD COLUMN outcome_policy jsonb NULL;
