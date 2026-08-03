CREATE TABLE organogram.snapshots (
    organization_id text NOT NULL,
    registry_version bigint NOT NULL CHECK (registry_version > 0),
    registry_fingerprint text NOT NULL,
    imported_at_utc timestamptz NOT NULL,
    organization_name text NULL,
    root_unit_id text NOT NULL,
    root_position_id text NOT NULL,
    PRIMARY KEY (organization_id, registry_version)
);

CREATE TABLE organogram.units (
    organization_id text NOT NULL,
    registry_version bigint NOT NULL,
    unit_id text NOT NULL,
    name text NULL,
    parent_unit_id text NULL,
    leadership_position_id text NOT NULL,
    stable_order integer NOT NULL CHECK (stable_order >= 0),
    PRIMARY KEY (organization_id, registry_version, unit_id),
    UNIQUE (organization_id, registry_version, stable_order),
    FOREIGN KEY (organization_id, registry_version)
        REFERENCES organogram.snapshots (organization_id, registry_version)
        ON DELETE CASCADE
);

CREATE TABLE organogram.positions (
    organization_id text NOT NULL,
    registry_version bigint NOT NULL,
    position_id text NOT NULL,
    name text NULL,
    unit_id text NOT NULL,
    occupant_type text NOT NULL CHECK (occupant_type IN ('AiAgent', 'Human')),
    reports_to_position_id text NULL,
    stable_order integer NOT NULL CHECK (stable_order >= 0),
    PRIMARY KEY (organization_id, registry_version, position_id),
    UNIQUE (organization_id, registry_version, stable_order),
    FOREIGN KEY (organization_id, registry_version)
        REFERENCES organogram.snapshots (organization_id, registry_version)
        ON DELETE CASCADE
);

CREATE TABLE organogram.current_snapshots (
    organization_id text PRIMARY KEY,
    registry_version bigint NOT NULL,
    FOREIGN KEY (organization_id, registry_version)
        REFERENCES organogram.snapshots (organization_id, registry_version)
        ON DELETE RESTRICT
);
