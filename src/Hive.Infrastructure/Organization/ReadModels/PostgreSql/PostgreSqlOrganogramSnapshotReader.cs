using System.Collections.ObjectModel;
using System.Data;
using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Hive.Infrastructure.Organization.ReadModels.PostgreSql;

public sealed class PostgreSqlOrganogramSnapshotReader :
    IOrganogramSnapshotReader,
    IAsyncDisposable
{
    private readonly NpgsqlDataSource? _dataSource;

    public PostgreSqlOrganogramSnapshotReader(IConfiguration configuration)
        : this(ConnectionString(configuration))
    {
    }

    internal PostgreSqlOrganogramSnapshotReader(string? connectionString)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            _dataSource = NpgsqlDataSource.Create(connectionString);
        }
    }

    public bool IsAvailable => _dataSource is not null;

    public async ValueTask<OrganogramSnapshot?> FindAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        cancellationToken.ThrowIfCancellationRequested();
        if (_dataSource is null)
        {
            throw new InvalidOperationException("The organogram read model is not configured.");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);
        var header = await LoadHeaderAsync(
            connection,
            transaction,
            organizationId,
            cancellationToken);
        if (header is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var units = await LoadUnitsAsync(
            connection,
            transaction,
            organizationId,
            header.RegistryVersion,
            cancellationToken);
        var positions = await LoadPositionsAsync(
            connection,
            transaction,
            organizationId,
            header.RegistryVersion,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new OrganogramSnapshot(
            organizationId.Value,
            header.RegistryVersion,
            header.RegistryFingerprint,
            header.ImportedAtUtc.ToUniversalTime(),
            header.OrganizationName,
            header.RootUnitId,
            header.RootPositionId,
            units,
            positions);
    }

    public ValueTask DisposeAsync() =>
        _dataSource is null ? ValueTask.CompletedTask : _dataSource.DisposeAsync();

    private static async Task<SnapshotHeader?> LoadHeaderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrganizationId organizationId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT snapshot.registry_version,
                   snapshot.registry_fingerprint,
                   snapshot.imported_at_utc,
                   snapshot.organization_name,
                   snapshot.root_unit_id,
                   snapshot.root_position_id
            FROM organogram.current_snapshots current
            INNER JOIN organogram.snapshots snapshot
                ON snapshot.organization_id = current.organization_id
               AND snapshot.registry_version = current.registry_version
            WHERE current.organization_id = @organization_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("organization_id", organizationId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SnapshotHeader(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetFieldValue<DateTimeOffset>(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5));
    }

    private static async Task<IReadOnlyList<OrganogramUnitSnapshot>> LoadUnitsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrganizationId organizationId,
        long registryVersion,
        CancellationToken cancellationToken)
    {
        var units = new List<OrganogramUnitSnapshot>();
        await using var command = VersionedCommand(
            """
            SELECT unit_id,
                   name,
                   parent_unit_id,
                   leadership_position_id
            FROM organogram.units
            WHERE organization_id = @organization_id
              AND registry_version = @registry_version
            ORDER BY stable_order;
            """,
            connection,
            transaction,
            organizationId,
            registryVersion);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            units.Add(new OrganogramUnitSnapshot(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3)));
        }

        return new ReadOnlyCollection<OrganogramUnitSnapshot>(units);
    }

    private static async Task<IReadOnlyList<OrganogramPositionSnapshot>> LoadPositionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrganizationId organizationId,
        long registryVersion,
        CancellationToken cancellationToken)
    {
        var positions = new List<OrganogramPositionSnapshot>();
        await using var command = VersionedCommand(
            """
            SELECT position_id,
                   name,
                   unit_id,
                   occupant_type,
                   reports_to_position_id
            FROM organogram.positions
            WHERE organization_id = @organization_id
              AND registry_version = @registry_version
            ORDER BY stable_order;
            """,
            connection,
            transaction,
            organizationId,
            registryVersion);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            positions.Add(new OrganogramPositionSnapshot(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                ParseOccupantType(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return new ReadOnlyCollection<OrganogramPositionSnapshot>(positions);
    }

    private static OccupantType ParseOccupantType(string value) =>
        Enum.TryParse<OccupantType>(value, ignoreCase: false, out var parsed) &&
        Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Unknown materialized occupant type '{value}'.");

    private static NpgsqlCommand VersionedCommand(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrganizationId organizationId,
        long registryVersion)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("organization_id", organizationId.Value);
        command.Parameters.AddWithValue("registry_version", registryVersion);
        return command;
    }

    private static string? ConnectionString(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetConnectionString(ConnectionStringNames.PostgreSql);
    }

    private sealed record SnapshotHeader(
        long RegistryVersion,
        string RegistryFingerprint,
        DateTimeOffset ImportedAtUtc,
        string? OrganizationName,
        string RootUnitId,
        string RootPositionId);
}
