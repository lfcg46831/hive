namespace Hive.Infrastructure.Persistence.PostgreSql;

/// <summary>
/// Name of the dedicated PostgreSQL schema that backs Akka.Persistence for the position subsystem
/// (US-F0-06-T05a). The journal and snapshot store live in their own <see cref="SchemaName"/> schema,
/// isolated from the other subsystems that share <c>ConnectionStrings:PostgreSql</c> (registry,
/// audit, read models, budgets, scheduler), so each subsystem owns its schema and migrations. The
/// Akka.Persistence.Sql journal/snapshot HOCON in <c>Hive.Actors</c>, the migration that creates the
/// schema, and the persistence health check all resolve the schema name from this single place so
/// they cannot drift. The individual table names are owned by the plugin (created by
/// auto-initialization) and intentionally not pinned here. The schema name is a durable contract and
/// must not change while journals exist.
/// </summary>
public static class PositionPersistenceSchema
{
    /// <summary>Dedicated schema owning the Akka journal and snapshot store tables.</summary>
    public const string SchemaName = "persistence";
}
