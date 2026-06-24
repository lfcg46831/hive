CREATE TABLE registry.organization_import_locks (
    organization_id text PRIMARY KEY
);

CREATE TABLE registry.organizations (
    organization_id text PRIMARY KEY,
    configuration_version bigint NOT NULL CHECK (configuration_version > 0),
    configuration_fingerprint text NOT NULL,
    imported_at timestamptz NOT NULL,
    name text NULL,
    root_unit_id text NOT NULL,
    owner_type text NOT NULL,
    owner_ref text NOT NULL,
    prompts jsonb NOT NULL,
    entry_fingerprint text NOT NULL,
    updated_at timestamptz NOT NULL
);

CREATE TABLE registry.units (
    organization_id text NOT NULL REFERENCES registry.organizations (organization_id) ON DELETE CASCADE,
    unit_id text NOT NULL,
    name text NULL,
    parent_unit_id text NULL,
    leadership_position_id text NOT NULL,
    entry_fingerprint text NOT NULL,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (organization_id, unit_id)
);

CREATE TABLE registry.positions (
    organization_id text NOT NULL REFERENCES registry.organizations (organization_id) ON DELETE CASCADE,
    position_id text NOT NULL,
    name text NULL,
    unit_id text NOT NULL,
    reports_to_position_id text NULL,
    timezone text NULL,
    entry_fingerprint text NOT NULL,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (organization_id, position_id),
    FOREIGN KEY (organization_id, unit_id)
        REFERENCES registry.units (organization_id, unit_id)
        ON DELETE RESTRICT
);

CREATE TABLE registry.occupants (
    organization_id text NOT NULL,
    position_id text NOT NULL,
    occupant_type text NOT NULL,
    identity_prompt_ref text NULL,
    ai jsonb NULL,
    working_hours jsonb NULL,
    subscriptions jsonb NOT NULL,
    tools jsonb NOT NULL,
    entry_fingerprint text NOT NULL,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (organization_id, position_id),
    FOREIGN KEY (organization_id, position_id)
        REFERENCES registry.positions (organization_id, position_id)
        ON DELETE CASCADE
);

CREATE TABLE registry.authorities (
    organization_id text NOT NULL,
    position_id text NOT NULL,
    can_decide jsonb NOT NULL,
    must_escalate jsonb NOT NULL,
    requires_human_approval jsonb NOT NULL,
    entry_fingerprint text NOT NULL,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (organization_id, position_id),
    FOREIGN KEY (organization_id, position_id)
        REFERENCES registry.positions (organization_id, position_id)
        ON DELETE CASCADE
);

CREATE TABLE registry.schedules (
    organization_id text NOT NULL,
    position_id text NOT NULL,
    schedule_id text NOT NULL,
    cron text NOT NULL,
    instruction text NOT NULL,
    entry_fingerprint text NOT NULL,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (organization_id, position_id, schedule_id),
    FOREIGN KEY (organization_id, position_id)
        REFERENCES registry.positions (organization_id, position_id)
        ON DELETE CASCADE
);

CREATE TABLE registry.command_relations (
    organization_id text PRIMARY KEY REFERENCES registry.organizations (organization_id) ON DELETE CASCADE,
    root_unit_leadership_position_id text NOT NULL,
    entry_fingerprint text NOT NULL,
    updated_at timestamptz NOT NULL
);
