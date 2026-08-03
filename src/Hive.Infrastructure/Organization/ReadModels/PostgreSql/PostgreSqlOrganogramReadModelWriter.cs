using Hive.Infrastructure.Organization.Registry;
using Npgsql;

namespace Hive.Infrastructure.Organization.ReadModels.PostgreSql;

internal static class PostgreSqlOrganogramReadModelWriter
{
    public static async Task PublishAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrganizationRegistrySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(snapshot);

        await InsertSnapshotAsync(connection, transaction, snapshot, cancellationToken);
        await InsertUnitsAsync(connection, transaction, snapshot, cancellationToken);
        await InsertPositionsAsync(connection, transaction, snapshot, cancellationToken);
        await SynchronizePositionStatesAsync(connection, transaction, snapshot, cancellationToken);
        await PublishCurrentVersionAsync(connection, transaction, snapshot, cancellationToken);
        await DeleteSupersededVersionsAsync(connection, transaction, snapshot, cancellationToken);
    }

    private static async Task InsertSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrganizationRegistrySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO organogram.snapshots (
                organization_id,
                registry_version,
                registry_fingerprint,
                imported_at_utc,
                organization_name,
                root_unit_id,
                root_position_id)
            VALUES (
                @organization_id,
                @registry_version,
                @registry_fingerprint,
                @imported_at_utc,
                @organization_name,
                @root_unit_id,
                @root_position_id);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("organization_id", snapshot.OrganizationId.Value);
        command.Parameters.AddWithValue("registry_version", snapshot.Version);
        command.Parameters.AddWithValue("registry_fingerprint", snapshot.Fingerprint);
        command.Parameters.AddWithValue("imported_at_utc", snapshot.ImportedAt);
        command.Parameters.AddWithValue(
            "organization_name",
            (object?)snapshot.Organization.Value.Name ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "root_unit_id",
            snapshot.Organization.Value.RootUnit.Value);
        command.Parameters.AddWithValue(
            "root_position_id",
            snapshot.Relations.Value.RootUnitLeadership.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertUnitsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrganizationRegistrySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var stableOrder = 0;
        foreach (var entry in snapshot.Units.Values.OrderBy(
                     entry => entry.Value.Id.Value,
                     StringComparer.Ordinal))
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO organogram.units (
                    organization_id,
                    registry_version,
                    unit_id,
                    name,
                    parent_unit_id,
                    leadership_position_id,
                    stable_order)
                VALUES (
                    @organization_id,
                    @registry_version,
                    @unit_id,
                    @name,
                    @parent_unit_id,
                    @leadership_position_id,
                    @stable_order);
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("organization_id", snapshot.OrganizationId.Value);
            command.Parameters.AddWithValue("registry_version", snapshot.Version);
            command.Parameters.AddWithValue("unit_id", entry.Value.Id.Value);
            command.Parameters.AddWithValue("name", (object?)entry.Value.Name ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "parent_unit_id",
                (object?)entry.Value.Parent?.Value ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "leadership_position_id",
                entry.Value.Leadership.Value);
            command.Parameters.AddWithValue("stable_order", stableOrder++);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertPositionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrganizationRegistrySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var stableOrder = 0;
        foreach (var entry in snapshot.Positions.Values.OrderBy(
                     entry => entry.Value.Id.Value,
                     StringComparer.Ordinal))
        {
            var occupant = snapshot.Occupants[entry.Value.Id].Value;
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO organogram.positions (
                    organization_id,
                    registry_version,
                    position_id,
                    name,
                    unit_id,
                    occupant_type,
                    reports_to_position_id,
                    stable_order)
                VALUES (
                    @organization_id,
                    @registry_version,
                    @position_id,
                    @name,
                    @unit_id,
                    @occupant_type,
                    @reports_to_position_id,
                    @stable_order);
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("organization_id", snapshot.OrganizationId.Value);
            command.Parameters.AddWithValue("registry_version", snapshot.Version);
            command.Parameters.AddWithValue("position_id", entry.Value.Id.Value);
            command.Parameters.AddWithValue("name", (object?)entry.Value.Name ?? DBNull.Value);
            command.Parameters.AddWithValue("unit_id", entry.Value.Unit.Value);
            command.Parameters.AddWithValue("occupant_type", occupant.Type.ToString());
            command.Parameters.AddWithValue(
                "reports_to_position_id",
                (object?)entry.Value.ReportsTo?.Value ?? DBNull.Value);
            command.Parameters.AddWithValue("stable_order", stableOrder++);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task PublishCurrentVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrganizationRegistrySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO organogram.current_snapshots (organization_id, registry_version)
            VALUES (@organization_id, @registry_version)
            ON CONFLICT (organization_id) DO UPDATE SET
                registry_version = EXCLUDED.registry_version;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("organization_id", snapshot.OrganizationId.Value);
        command.Parameters.AddWithValue("registry_version", snapshot.Version);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SynchronizePositionStatesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrganizationRegistrySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO organogram.position_states (
                organization_id,
                position_id,
                state,
                sequence,
                updated_at_utc)
            SELECT organization_id,
                   position_id,
                   'Idle',
                   0,
                   @imported_at_utc
            FROM organogram.positions
            WHERE organization_id = @organization_id
              AND registry_version = @registry_version
            ON CONFLICT (organization_id, position_id) DO NOTHING;

            DELETE FROM organogram.position_states state
            WHERE state.organization_id = @organization_id
              AND NOT EXISTS (
                  SELECT 1
                  FROM organogram.positions position
                  WHERE position.organization_id = @organization_id
                    AND position.registry_version = @registry_version
                    AND position.position_id = state.position_id);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("organization_id", snapshot.OrganizationId.Value);
        command.Parameters.AddWithValue("registry_version", snapshot.Version);
        command.Parameters.AddWithValue("imported_at_utc", snapshot.ImportedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteSupersededVersionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrganizationRegistrySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            DELETE FROM organogram.snapshots
            WHERE organization_id = @organization_id
              AND registry_version <> @registry_version;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("organization_id", snapshot.OrganizationId.Value);
        command.Parameters.AddWithValue("registry_version", snapshot.Version);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
