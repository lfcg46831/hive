ALTER TABLE registry.units
    ADD COLUMN allowed_peer_channels jsonb NOT NULL DEFAULT '[]'::jsonb
    CHECK (jsonb_typeof(allowed_peer_channels) = 'array');
