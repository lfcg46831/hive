-- Akka.Persistence (Akka.Persistence.Sql) journal and snapshot store for the PositionActor
-- (US-F0-06-T05a). This migration owns and versions the dedicated `persistence` schema; the
-- journal/snapshot/tag/metadata table DDL is owned by the plugin and created by auto-initialization
-- inside this schema on first use, so it is intentionally not declared here. The migrator bootstrap
-- also ensures the schema exists for its own ledger; recreating it here is an idempotent no-op that
-- keeps the dedicated schema an explicit, versioned part of the subsystem's migrations.

CREATE SCHEMA IF NOT EXISTS persistence;
