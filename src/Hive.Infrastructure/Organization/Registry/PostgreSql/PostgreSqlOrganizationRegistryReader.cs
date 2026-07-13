using System.Collections.ObjectModel;
using Hive.Domain.Identity;
using Hive.Domain.Governance;
using Hive.Domain.Messaging;
using Hive.Domain.Organization;
using Hive.Domain.Organization.Configuration;
using Npgsql;

namespace Hive.Infrastructure.Organization.Registry.PostgreSql;

internal static class PostgreSqlOrganizationRegistryReader
{
    public static async Task<OrganizationRegistrySnapshot?> LoadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        OrganizationId organizationId,
        CancellationToken cancellationToken)
    {
        var header = await LoadHeaderAsync(
            connection,
            transaction,
            organizationId,
            cancellationToken);
        if (header is null)
        {
            return null;
        }

        var units = await LoadUnitsAsync(connection, transaction, organizationId, cancellationToken);
        var positions = await LoadPositionsAsync(connection, transaction, organizationId, cancellationToken);
        var occupants = await LoadOccupantsAsync(connection, transaction, organizationId, cancellationToken);
        var authorities = await LoadAuthoritiesAsync(connection, transaction, organizationId, cancellationToken);
        var schedules = await LoadSchedulesAsync(connection, transaction, organizationId, cancellationToken);
        var relations = await LoadRelationsAsync(
            connection,
            transaction,
            organizationId,
            positions,
            cancellationToken);

        return new OrganizationRegistrySnapshot(
            organizationId,
            header.Version,
            header.ConfigurationFingerprint,
            header.ImportedAt,
            header.Organization,
            units,
            positions,
            occupants,
            authorities,
            schedules,
            relations,
            header.ActionDomainCatalog);
    }

    private static async Task<Header?> LoadHeaderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        OrganizationId organizationId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT configuration_version,
                   configuration_fingerprint,
                   imported_at,
                   name,
                   root_unit_id,
                   owner_type,
                   owner_ref,
                   prompts::text,
                   action_domain_catalog::text,
                   action_domain_catalog_fingerprint,
                   action_domain_catalog_updated_at,
                   entry_fingerprint,
                   updated_at
            FROM registry.organizations
            WHERE organization_id = @organization_id;
            """,
            connection,
            transaction,
            organizationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var ownerType = ParseEnum<OwnerType>(reader.GetString(5), "owner_type");
        var value = new RegistryOrganization(
            organizationId,
            reader.IsDBNull(3) ? null : reader.GetString(3),
            UnitId.From(reader.GetString(4)),
            new OwnerConfiguration(ownerType, reader.GetString(6)),
            Array.AsReadOnly(RegistryJson.Deserialize<PromptConfiguration[]>(reader.GetString(7))));

        var actionDomainCatalog = reader.IsDBNull(8) || reader.IsDBNull(9) || reader.IsDBNull(10)
            ? new RegistryEntry<ActionDomainCatalog>(
                new ActionDomainCatalog(
                    1,
                    new ActionDomainCatalogDefaults(ActionDomainGate.Escalate),
                    []),
                "missing",
                reader.GetFieldValue<DateTimeOffset>(2))
            : new RegistryEntry<ActionDomainCatalog>(
                RegistryJson.DeserializeActionDomainCatalog(reader.GetString(8)),
                reader.GetString(9),
                reader.GetFieldValue<DateTimeOffset>(10));

        return new Header(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetFieldValue<DateTimeOffset>(2),
            new RegistryEntry<RegistryOrganization>(
                value,
                reader.GetString(11),
                reader.GetFieldValue<DateTimeOffset>(12)),
            actionDomainCatalog);
    }

    private static async Task<IReadOnlyDictionary<UnitId, RegistryEntry<RegistryUnit>>> LoadUnitsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        OrganizationId organizationId,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<UnitId, RegistryEntry<RegistryUnit>>();
        await using var command = CreateCommand(
            """
            SELECT unit_id, name, parent_unit_id, leadership_position_id, entry_fingerprint, updated_at
            FROM registry.units
            WHERE organization_id = @organization_id
            ORDER BY unit_id;
            """,
            connection,
            transaction,
            organizationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = UnitId.From(reader.GetString(0));
            result.Add(
                id,
                new RegistryEntry<RegistryUnit>(
                    new RegistryUnit(
                        id,
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : UnitId.From(reader.GetString(2)),
                        PositionId.From(reader.GetString(3))),
                    reader.GetString(4),
                    reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return new ReadOnlyDictionary<UnitId, RegistryEntry<RegistryUnit>>(result);
    }

    private static async Task<IReadOnlyDictionary<PositionId, RegistryEntry<RegistryPosition>>> LoadPositionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        OrganizationId organizationId,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<PositionId, RegistryEntry<RegistryPosition>>();
        await using var command = CreateCommand(
            """
            SELECT position_id, name, unit_id, reports_to_position_id, timezone, entry_fingerprint, updated_at
            FROM registry.positions
            WHERE organization_id = @organization_id
            ORDER BY position_id;
            """,
            connection,
            transaction,
            organizationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = PositionId.From(reader.GetString(0));
            result.Add(
                id,
                new RegistryEntry<RegistryPosition>(
                    new RegistryPosition(
                        id,
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        UnitId.From(reader.GetString(2)),
                        reader.IsDBNull(3) ? null : PositionId.From(reader.GetString(3)),
                        reader.IsDBNull(4) ? null : reader.GetString(4)),
                    reader.GetString(5),
                    reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return new ReadOnlyDictionary<PositionId, RegistryEntry<RegistryPosition>>(result);
    }

    private static async Task<IReadOnlyDictionary<PositionId, RegistryEntry<RegistryOccupant>>> LoadOccupantsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        OrganizationId organizationId,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<PositionId, RegistryEntry<RegistryOccupant>>();
        await using var command = CreateCommand(
            """
            SELECT position_id,
                   occupant_type,
                   identity_prompt_ref,
                   ai::text,
                   working_hours::text,
                   subscriptions::text,
                   tools::text,
                   entry_fingerprint,
                   updated_at
            FROM registry.occupants
            WHERE organization_id = @organization_id
            ORDER BY position_id;
            """,
            connection,
            transaction,
            organizationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = PositionId.From(reader.GetString(0));
            var value = new RegistryOccupant(
                id,
                ParseEnum<OccupantType>(reader.GetString(1), "occupant_type"),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : RegistryJson.Deserialize<AiConfiguration>(reader.GetString(3)),
                reader.IsDBNull(4)
                    ? null
                    : RegistryJson.Deserialize<WorkingHoursConfiguration>(reader.GetString(4)),
                Array.AsReadOnly(RegistryJson.Deserialize<SubscriptionConfiguration[]>(reader.GetString(5))),
                Array.AsReadOnly(RegistryJson.Deserialize<ToolConfiguration[]>(reader.GetString(6))));
            result.Add(
                id,
                new RegistryEntry<RegistryOccupant>(
                    value,
                    reader.GetString(7),
                    reader.GetFieldValue<DateTimeOffset>(8)));
        }

        return new ReadOnlyDictionary<PositionId, RegistryEntry<RegistryOccupant>>(result);
    }

    private static async Task<IReadOnlyDictionary<PositionId, RegistryEntry<RegistryAuthority>>> LoadAuthoritiesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        OrganizationId organizationId,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<PositionId, RegistryEntry<RegistryAuthority>>();
        await using var command = CreateCommand(
            """
            SELECT position_id,
                   can_decide::text,
                   overrides::text,
                   entry_fingerprint,
                   updated_at
            FROM registry.authorities
            WHERE organization_id = @organization_id
            ORDER BY position_id;
            """,
            connection,
            transaction,
            organizationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = PositionId.From(reader.GetString(0));
            var value = new RegistryAuthority(
                id,
                Array.AsReadOnly(RegistryJson.Deserialize<string[]>(reader.GetString(1))),
                Array.AsReadOnly(RegistryJson.Deserialize<RegistryAuthorityOverride[]>(reader.GetString(2))));
            result.Add(
                id,
                new RegistryEntry<RegistryAuthority>(
                    value,
                    reader.GetString(3),
                    reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return new ReadOnlyDictionary<PositionId, RegistryEntry<RegistryAuthority>>(result);
    }

    private static async Task<IReadOnlyDictionary<RegistryScheduleKey, RegistryEntry<RegistrySchedule>>> LoadSchedulesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        OrganizationId organizationId,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<RegistryScheduleKey, RegistryEntry<RegistrySchedule>>();
        await using var command = CreateCommand(
            """
            SELECT position_id, schedule_id, active, cron, priority, critical, catch_up, instruction, entry_fingerprint, updated_at
            FROM registry.schedules
            WHERE organization_id = @organization_id
            ORDER BY position_id, schedule_id;
            """,
            connection,
            transaction,
            organizationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var positionId = PositionId.From(reader.GetString(0));
            var scheduleId = reader.GetString(1);
            var key = new RegistryScheduleKey(positionId, scheduleId);
            result.Add(
                key,
                new RegistryEntry<RegistrySchedule>(
                    new RegistrySchedule(
                        positionId,
                        scheduleId,
                        reader.GetBoolean(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetBoolean(5),
                        reader.GetString(6),
                        reader.GetString(7)),
                    reader.GetString(8),
                    reader.GetFieldValue<DateTimeOffset>(9)));
        }

        return new ReadOnlyDictionary<RegistryScheduleKey, RegistryEntry<RegistrySchedule>>(result);
    }

    private static async Task<RegistryEntry<OrganizationRelationsSnapshot>> LoadRelationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        OrganizationId organizationId,
        IReadOnlyDictionary<PositionId, RegistryEntry<RegistryPosition>> positions,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT root_unit_leadership_position_id, entry_fingerprint, updated_at
            FROM registry.command_relations
            WHERE organization_id = @organization_id;
            """,
            connection,
            transaction,
            organizationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                $"Registry organization '{organizationId.Value}' has no command-relations row.");
        }

        var persistedRoot = PositionId.From(reader.GetString(0));
        var builder = OrganizationRelationsSnapshot.CreateBuilder(
            organizationId,
            new OrganizationOwnerEndpointRef());
        foreach (var position in positions.Values.Select(entry => entry.Value))
        {
            builder.AddPosition(position.Id, position.Unit, position.ReportsTo);
        }

        var relations = builder.Build();
        if (relations.RootUnitLeadership != persistedRoot)
        {
            throw new InvalidDataException(
                $"Registry organization '{organizationId.Value}' has inconsistent root-unit leadership.");
        }

        return new RegistryEntry<OrganizationRelationsSnapshot>(
            relations,
            reader.GetString(1),
            reader.GetFieldValue<DateTimeOffset>(2));
    }

    private static NpgsqlCommand CreateCommand(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        OrganizationId organizationId)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("organization_id", organizationId.Value);
        return command;
    }

    private static T ParseEnum<T>(string value, string column)
        where T : struct, Enum
    {
        if (Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        throw new InvalidDataException(
            $"Registry column '{column}' contains unknown {typeof(T).Name} value '{value}'.");
    }

    private sealed record Header(
        long Version,
        string ConfigurationFingerprint,
        DateTimeOffset ImportedAt,
        RegistryEntry<RegistryOrganization> Organization,
        RegistryEntry<ActionDomainCatalog> ActionDomainCatalog);
}
