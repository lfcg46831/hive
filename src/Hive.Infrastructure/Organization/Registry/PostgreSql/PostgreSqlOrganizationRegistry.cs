using System.Data;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization;
using Npgsql;

namespace Hive.Infrastructure.Organization.Registry.PostgreSql;

public sealed class PostgreSqlOrganizationRegistry :
    IOrganizationRegistryReader,
    IOrganizationRegistryStore,
    IOrganizationRelations
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlOrganizationRegistry(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async ValueTask<OrganizationRegistrySnapshot?> FindSnapshotAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);
        var snapshot = await PostgreSqlOrganizationRegistryReader.LoadAsync(
            connection,
            transaction,
            organizationId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return snapshot;
    }

    async ValueTask<OrganizationImportResult> IOrganizationRegistryStore.ApplyAsync(
        OrganizationRegistryProjection target,
        DateTimeOffset importedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await LockOrganizationAsync(
            connection,
            transaction,
            target.OrganizationId,
            cancellationToken);
        var current = await PostgreSqlOrganizationRegistryReader.LoadAsync(
            connection,
            transaction,
            target.OrganizationId,
            cancellationToken);
        var result = OrganizationRegistryMutation.Apply(current, target, importedAt);

        if (result.Status == OrganizationImportStatus.Applied)
        {
            await PostgreSqlOrganizationRegistryWriter.WriteAsync(
                connection,
                transaction,
                current,
                result,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async ValueTask<PositionId?> GetDirectSuperiorAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(positionId);
        var relations = await RequireRelationsAsync(organizationId, cancellationToken);
        RequirePosition(relations, positionId);
        return relations.GetDirectSuperior(positionId);
    }

    public async ValueTask<IReadOnlyCollection<PositionId>> GetDirectSubordinatesAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(positionId);
        var relations = await RequireRelationsAsync(organizationId, cancellationToken);
        RequirePosition(relations, positionId);
        return relations.GetDirectSubordinates(positionId);
    }

    public async ValueTask<PositionId> GetRootUnitLeadershipAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default) =>
        (await RequireRelationsAsync(organizationId, cancellationToken)).RootUnitLeadership;

    public async ValueTask<OrganizationOwnerEndpointRef> GetOrganizationOwnerAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default) =>
        (await RequireRelationsAsync(organizationId, cancellationToken)).Owner;

    public async ValueTask<UnitId?> GetUnitOfPositionAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(positionId);
        var relations = await RequireRelationsAsync(organizationId, cancellationToken);
        return relations.GetUnit(positionId);
    }

    private static async Task LockOrganizationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrganizationId organizationId,
        CancellationToken cancellationToken)
    {
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO registry.organization_import_locks (organization_id)
            VALUES (@organization_id)
            ON CONFLICT DO NOTHING;
            """,
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("organization_id", organizationId.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var acquire = new NpgsqlCommand(
            """
            SELECT organization_id
            FROM registry.organization_import_locks
            WHERE organization_id = @organization_id
            FOR UPDATE;
            """,
            connection,
            transaction);
        acquire.Parameters.AddWithValue("organization_id", organizationId.Value);
        var locked = await acquire.ExecuteScalarAsync(cancellationToken);
        if (locked is null)
        {
            throw new InvalidOperationException(
                $"Could not acquire registry import lock for organization '{organizationId.Value}'.");
        }
    }

    private async Task<OrganizationRelationsSnapshot> RequireRelationsAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        var snapshot = await FindSnapshotAsync(organizationId, cancellationToken);
        if (snapshot is null)
        {
            throw OrganizationRelationNotFoundException.ForOrganization(organizationId);
        }

        return snapshot.Relations.Value;
    }

    private static void RequirePosition(
        OrganizationRelationsSnapshot relations,
        PositionId positionId)
    {
        if (!relations.ContainsPosition(positionId))
        {
            throw OrganizationRelationNotFoundException.ForPosition(
                relations.OrganizationId,
                positionId);
        }
    }
}
