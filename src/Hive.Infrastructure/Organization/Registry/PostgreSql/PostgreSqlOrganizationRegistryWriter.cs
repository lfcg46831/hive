using Hive.Domain.Identity;
using Npgsql;
using NpgsqlTypes;

namespace Hive.Infrastructure.Organization.Registry.PostgreSql;

internal static class PostgreSqlOrganizationRegistryWriter
{
    public static async Task WriteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrganizationRegistrySnapshot? current,
        OrganizationImportResult result,
        CancellationToken cancellationToken)
    {
        var snapshot = result.Snapshot
            ?? throw new ArgumentException("An applied import must contain a snapshot.", nameof(result));
        var plan = result.Plan
            ?? throw new ArgumentException("An applied import must contain a plan.", nameof(result));

        await UpsertOrganizationAsync(connection, transaction, snapshot, cancellationToken);

        var removed = plan.Changes.Where(change => change.Kind == RegistryChangeKind.Removed).ToArray();
        var upserts = plan.Changes.Where(change => change.Kind != RegistryChangeKind.Removed).ToArray();

        foreach (var change in removed.Where(change => change.EntityKind == RegistryEntityKind.Schedule))
        {
            var key = RequireCurrent(current?.Schedules, change);
            await DeleteScheduleAsync(connection, transaction, snapshot.OrganizationId, key, cancellationToken);
        }

        foreach (var change in removed.Where(change => change.EntityKind == RegistryEntityKind.Authority))
        {
            var id = RequireCurrent(current?.Authorities, change);
            await DeletePositionChildAsync(
                connection,
                transaction,
                RegistryEntityKind.Authority,
                snapshot.OrganizationId,
                id,
                cancellationToken);
        }

        foreach (var change in removed.Where(change => change.EntityKind == RegistryEntityKind.Occupant))
        {
            var id = RequireCurrent(current?.Occupants, change);
            await DeletePositionChildAsync(
                connection,
                transaction,
                RegistryEntityKind.Occupant,
                snapshot.OrganizationId,
                id,
                cancellationToken);
        }

        foreach (var change in removed.Where(change => change.EntityKind == RegistryEntityKind.Position))
        {
            var id = RequireCurrent(current?.Positions, change);
            await DeletePositionAsync(connection, transaction, snapshot.OrganizationId, id, cancellationToken);
        }

        foreach (var change in upserts.Where(change => change.EntityKind == RegistryEntityKind.Unit))
        {
            await UpsertUnitAsync(
                connection,
                transaction,
                snapshot.OrganizationId,
                RequireTarget(snapshot.Units, change),
                cancellationToken);
        }

        foreach (var change in upserts.Where(change => change.EntityKind == RegistryEntityKind.Position))
        {
            await UpsertPositionAsync(
                connection,
                transaction,
                snapshot.OrganizationId,
                RequireTarget(snapshot.Positions, change),
                cancellationToken);
        }

        foreach (var change in upserts.Where(change => change.EntityKind == RegistryEntityKind.Occupant))
        {
            await UpsertOccupantAsync(
                connection,
                transaction,
                snapshot.OrganizationId,
                RequireTarget(snapshot.Occupants, change),
                cancellationToken);
        }

        foreach (var change in upserts.Where(change => change.EntityKind == RegistryEntityKind.Authority))
        {
            await UpsertAuthorityAsync(
                connection,
                transaction,
                snapshot.OrganizationId,
                RequireTarget(snapshot.Authorities, change),
                cancellationToken);
        }

        foreach (var change in upserts.Where(change => change.EntityKind == RegistryEntityKind.Schedule))
        {
            await UpsertScheduleAsync(
                connection,
                transaction,
                snapshot.OrganizationId,
                RequireTarget(snapshot.Schedules, change),
                cancellationToken);
        }

        if (upserts.Any(change => change.EntityKind == RegistryEntityKind.CommandRelations))
        {
            await UpsertRelationsAsync(connection, transaction, snapshot, cancellationToken);
        }

        foreach (var change in removed.Where(change => change.EntityKind == RegistryEntityKind.Unit))
        {
            var id = RequireCurrent(current?.Units, change);
            await DeleteUnitAsync(connection, transaction, snapshot.OrganizationId, id, cancellationToken);
        }
    }

    private static async Task UpsertOrganizationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrganizationRegistrySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var entry = snapshot.Organization;
        var value = entry.Value;
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO registry.organizations (
                organization_id,
                configuration_version,
                configuration_fingerprint,
                imported_at,
                name,
                root_unit_id,
                owner_type,
                owner_ref,
                prompts,
                entry_fingerprint,
                updated_at)
            VALUES (
                @organization_id,
                @configuration_version,
                @configuration_fingerprint,
                @imported_at,
                @name,
                @root_unit_id,
                @owner_type,
                @owner_ref,
                @prompts,
                @entry_fingerprint,
                @updated_at)
            ON CONFLICT (organization_id) DO UPDATE SET
                configuration_version = EXCLUDED.configuration_version,
                configuration_fingerprint = EXCLUDED.configuration_fingerprint,
                imported_at = EXCLUDED.imported_at,
                name = EXCLUDED.name,
                root_unit_id = EXCLUDED.root_unit_id,
                owner_type = EXCLUDED.owner_type,
                owner_ref = EXCLUDED.owner_ref,
                prompts = EXCLUDED.prompts,
                entry_fingerprint = EXCLUDED.entry_fingerprint,
                updated_at = EXCLUDED.updated_at;
            """,
            connection,
            transaction);
        AddText(command, "organization_id", snapshot.OrganizationId.Value);
        command.Parameters.AddWithValue("configuration_version", snapshot.Version);
        AddText(command, "configuration_fingerprint", snapshot.Fingerprint);
        AddTimestamp(command, "imported_at", snapshot.ImportedAt);
        AddText(command, "name", value.Name);
        AddText(command, "root_unit_id", value.RootUnit.Value);
        AddText(command, "owner_type", value.Owner.Type.ToString());
        AddText(command, "owner_ref", value.Owner.Ref);
        AddJson(command, "prompts", value.Prompts);
        AddText(command, "entry_fingerprint", entry.Fingerprint);
        AddTimestamp(command, "updated_at", entry.UpdatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertUnitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrganizationId organizationId,
        RegistryEntry<RegistryUnit> entry,
        CancellationToken cancellationToken)
    {
        var value = entry.Value;
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO registry.units (
                organization_id, unit_id, name, parent_unit_id, leadership_position_id,
                entry_fingerprint, updated_at)
            VALUES (
                @organization_id, @unit_id, @name, @parent_unit_id, @leadership_position_id,
                @entry_fingerprint, @updated_at)
            ON CONFLICT (organization_id, unit_id) DO UPDATE SET
                name = EXCLUDED.name,
                parent_unit_id = EXCLUDED.parent_unit_id,
                leadership_position_id = EXCLUDED.leadership_position_id,
                entry_fingerprint = EXCLUDED.entry_fingerprint,
                updated_at = EXCLUDED.updated_at;
            """,
            connection,
            transaction);
        AddText(command, "organization_id", organizationId.Value);
        AddText(command, "unit_id", value.Id.Value);
        AddText(command, "name", value.Name);
        AddText(command, "parent_unit_id", value.Parent?.Value);
        AddText(command, "leadership_position_id", value.Leadership.Value);
        AddText(command, "entry_fingerprint", entry.Fingerprint);
        AddTimestamp(command, "updated_at", entry.UpdatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertPositionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrganizationId organizationId,
        RegistryEntry<RegistryPosition> entry,
        CancellationToken cancellationToken)
    {
        var value = entry.Value;
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO registry.positions (
                organization_id, position_id, name, unit_id, reports_to_position_id, timezone,
                entry_fingerprint, updated_at)
            VALUES (
                @organization_id, @position_id, @name, @unit_id, @reports_to_position_id, @timezone,
                @entry_fingerprint, @updated_at)
            ON CONFLICT (organization_id, position_id) DO UPDATE SET
                name = EXCLUDED.name,
                unit_id = EXCLUDED.unit_id,
                reports_to_position_id = EXCLUDED.reports_to_position_id,
                timezone = EXCLUDED.timezone,
                entry_fingerprint = EXCLUDED.entry_fingerprint,
                updated_at = EXCLUDED.updated_at;
            """,
            connection,
            transaction);
        AddText(command, "organization_id", organizationId.Value);
        AddText(command, "position_id", value.Id.Value);
        AddText(command, "name", value.Name);
        AddText(command, "unit_id", value.Unit.Value);
        AddText(command, "reports_to_position_id", value.ReportsTo?.Value);
        AddText(command, "timezone", value.Timezone);
        AddText(command, "entry_fingerprint", entry.Fingerprint);
        AddTimestamp(command, "updated_at", entry.UpdatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertOccupantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrganizationId organizationId,
        RegistryEntry<RegistryOccupant> entry,
        CancellationToken cancellationToken)
    {
        var value = entry.Value;
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO registry.occupants (
                organization_id, position_id, occupant_type, identity_prompt_ref, ai, working_hours,
                subscriptions, tools, entry_fingerprint, updated_at)
            VALUES (
                @organization_id, @position_id, @occupant_type, @identity_prompt_ref, @ai, @working_hours,
                @subscriptions, @tools, @entry_fingerprint, @updated_at)
            ON CONFLICT (organization_id, position_id) DO UPDATE SET
                occupant_type = EXCLUDED.occupant_type,
                identity_prompt_ref = EXCLUDED.identity_prompt_ref,
                ai = EXCLUDED.ai,
                working_hours = EXCLUDED.working_hours,
                subscriptions = EXCLUDED.subscriptions,
                tools = EXCLUDED.tools,
                entry_fingerprint = EXCLUDED.entry_fingerprint,
                updated_at = EXCLUDED.updated_at;
            """,
            connection,
            transaction);
        AddText(command, "organization_id", organizationId.Value);
        AddText(command, "position_id", value.PositionId.Value);
        AddText(command, "occupant_type", value.Type.ToString());
        AddText(command, "identity_prompt_ref", value.IdentityPromptRef);
        AddJson(command, "ai", value.Ai);
        AddJson(command, "working_hours", value.WorkingHours);
        AddJson(command, "subscriptions", value.Subscriptions);
        AddJson(command, "tools", value.Tools);
        AddText(command, "entry_fingerprint", entry.Fingerprint);
        AddTimestamp(command, "updated_at", entry.UpdatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertAuthorityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrganizationId organizationId,
        RegistryEntry<RegistryAuthority> entry,
        CancellationToken cancellationToken)
    {
        var value = entry.Value;
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO registry.authorities (
                organization_id, position_id, can_decide, must_escalate, requires_human_approval,
                entry_fingerprint, updated_at)
            VALUES (
                @organization_id, @position_id, @can_decide, @must_escalate, @requires_human_approval,
                @entry_fingerprint, @updated_at)
            ON CONFLICT (organization_id, position_id) DO UPDATE SET
                can_decide = EXCLUDED.can_decide,
                must_escalate = EXCLUDED.must_escalate,
                requires_human_approval = EXCLUDED.requires_human_approval,
                entry_fingerprint = EXCLUDED.entry_fingerprint,
                updated_at = EXCLUDED.updated_at;
            """,
            connection,
            transaction);
        AddText(command, "organization_id", organizationId.Value);
        AddText(command, "position_id", value.PositionId.Value);
        AddJson(command, "can_decide", value.CanDecide);
        AddJson(command, "must_escalate", value.MustEscalate);
        AddJson(command, "requires_human_approval", value.RequiresHumanApproval);
        AddText(command, "entry_fingerprint", entry.Fingerprint);
        AddTimestamp(command, "updated_at", entry.UpdatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertScheduleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrganizationId organizationId,
        RegistryEntry<RegistrySchedule> entry,
        CancellationToken cancellationToken)
    {
        var value = entry.Value;
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO registry.schedules (
                organization_id, position_id, schedule_id, cron, instruction,
                entry_fingerprint, updated_at)
            VALUES (
                @organization_id, @position_id, @schedule_id, @cron, @instruction,
                @entry_fingerprint, @updated_at)
            ON CONFLICT (organization_id, position_id, schedule_id) DO UPDATE SET
                cron = EXCLUDED.cron,
                instruction = EXCLUDED.instruction,
                entry_fingerprint = EXCLUDED.entry_fingerprint,
                updated_at = EXCLUDED.updated_at;
            """,
            connection,
            transaction);
        AddText(command, "organization_id", organizationId.Value);
        AddText(command, "position_id", value.PositionId.Value);
        AddText(command, "schedule_id", value.ScheduleId);
        AddText(command, "cron", value.Cron);
        AddText(command, "instruction", value.Instruction);
        AddText(command, "entry_fingerprint", entry.Fingerprint);
        AddTimestamp(command, "updated_at", entry.UpdatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertRelationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrganizationRegistrySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var entry = snapshot.Relations;
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO registry.command_relations (
                organization_id, root_unit_leadership_position_id, entry_fingerprint, updated_at)
            VALUES (
                @organization_id, @root_unit_leadership_position_id, @entry_fingerprint, @updated_at)
            ON CONFLICT (organization_id) DO UPDATE SET
                root_unit_leadership_position_id = EXCLUDED.root_unit_leadership_position_id,
                entry_fingerprint = EXCLUDED.entry_fingerprint,
                updated_at = EXCLUDED.updated_at;
            """,
            connection,
            transaction);
        AddText(command, "organization_id", snapshot.OrganizationId.Value);
        AddText(command, "root_unit_leadership_position_id", entry.Value.RootUnitLeadership.Value);
        AddText(command, "entry_fingerprint", entry.Fingerprint);
        AddTimestamp(command, "updated_at", entry.UpdatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Task DeleteScheduleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrganizationId organizationId,
        RegistryScheduleKey key,
        CancellationToken cancellationToken) =>
        DeleteAsync(
            connection,
            transaction,
            "DELETE FROM registry.schedules WHERE organization_id = @organization_id AND position_id = @position_id AND schedule_id = @schedule_id;",
            organizationId,
            cancellationToken,
            ("position_id", key.PositionId.Value),
            ("schedule_id", key.ScheduleId));

    private static Task DeletePositionChildAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RegistryEntityKind entityKind,
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken) =>
        DeleteAsync(
            connection,
            transaction,
            entityKind switch
            {
                RegistryEntityKind.Authority =>
                    "DELETE FROM registry.authorities WHERE organization_id = @organization_id AND position_id = @position_id;",
                RegistryEntityKind.Occupant =>
                    "DELETE FROM registry.occupants WHERE organization_id = @organization_id AND position_id = @position_id;",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(entityKind),
                    entityKind,
                    "Only position-child entity kinds can be deleted by this method."),
            },
            organizationId,
            cancellationToken,
            ("position_id", positionId.Value));

    private static Task DeletePositionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken) =>
        DeleteAsync(
            connection,
            transaction,
            "DELETE FROM registry.positions WHERE organization_id = @organization_id AND position_id = @position_id;",
            organizationId,
            cancellationToken,
            ("position_id", positionId.Value));

    private static Task DeleteUnitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrganizationId organizationId,
        UnitId unitId,
        CancellationToken cancellationToken) =>
        DeleteAsync(
            connection,
            transaction,
            "DELETE FROM registry.units WHERE organization_id = @organization_id AND unit_id = @unit_id;",
            organizationId,
            cancellationToken,
            ("unit_id", unitId.Value));

    private static async Task DeleteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        OrganizationId organizationId,
        CancellationToken cancellationToken,
        params (string Name, string Value)[] keys)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddText(command, "organization_id", organizationId.Value);
        foreach (var (name, value) in keys)
        {
            AddText(command, name, value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static TKey RequireCurrent<TKey, TValue>(
        IReadOnlyDictionary<TKey, RegistryEntry<TValue>>? entries,
        OrganizationRegistryChange change)
        where TKey : notnull
    {
        if (entries is not null)
        {
            foreach (var key in entries.Keys)
            {
                if (KeyText(key) == change.Key)
                {
                    return key;
                }
            }
        }

        throw new InvalidOperationException(
            $"Current registry entry '{change.EntityKind}/{change.Key}' was not found for removal.");
    }

    private static RegistryEntry<TValue> RequireTarget<TKey, TValue>(
        IReadOnlyDictionary<TKey, RegistryEntry<TValue>> entries,
        OrganizationRegistryChange change)
        where TKey : notnull =>
        entries.SingleOrDefault(pair => KeyText(pair.Key) == change.Key).Value
        ?? throw new InvalidOperationException(
            $"Target registry entry '{change.EntityKind}/{change.Key}' was not found for upsert.");

    private static string KeyText<TKey>(TKey key) => key switch
    {
        UnitId unitId => unitId.Value,
        PositionId positionId => positionId.Value,
        RegistryScheduleKey scheduleKey => scheduleKey.ToString(),
        _ => key?.ToString() ?? string.Empty,
    };

    private static void AddText(NpgsqlCommand command, string name, string? value)
    {
        command.Parameters.Add(name, NpgsqlDbType.Text).Value = value ?? (object)DBNull.Value;
    }

    private static void AddJson<T>(NpgsqlCommand command, string name, T? value)
    {
        command.Parameters.Add(name, NpgsqlDbType.Jsonb).Value =
            value is null ? DBNull.Value : RegistryJson.Serialize(value);
    }

    private static void AddTimestamp(NpgsqlCommand command, string name, DateTimeOffset value)
    {
        command.Parameters.Add(name, NpgsqlDbType.TimestampTz).Value = value;
    }
}
