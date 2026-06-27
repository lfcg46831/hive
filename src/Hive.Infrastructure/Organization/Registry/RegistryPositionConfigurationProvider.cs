using Hive.Domain.Identity;
using Hive.Domain.Positions;

namespace Hive.Infrastructure.Organization.Registry;

/// <summary>
/// Loads the PositionActor runtime configuration from the materialized organization registry/read model
/// (US-F0-06-T08b).
/// </summary>
public sealed class RegistryPositionConfigurationProvider : IPositionConfigurationProvider
{
    private readonly IOrganizationRegistryReader _registryReader;

    public RegistryPositionConfigurationProvider(IOrganizationRegistryReader registryReader)
    {
        _registryReader = registryReader ?? throw new ArgumentNullException(nameof(registryReader));
    }

    public async Task<PositionRuntimeConfigurationLoadResult> LoadAsync(
        PositionEntityId entityId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entityId);

        OrganizationRegistrySnapshot? snapshot;
        try
        {
            snapshot = await _registryReader
                .FindSnapshotAsync(entityId.Organization, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return PositionRuntimeConfigurationLoadResult.TechnicalFailure(exception);
        }

        if (snapshot is null)
        {
            return PositionRuntimeConfigurationLoadResult.Missing(
                $"Organization '{entityId.Organization.Value}' was not found in the registry.");
        }

        return Project(snapshot, entityId);
    }

    private static PositionRuntimeConfigurationLoadResult Project(
        OrganizationRegistrySnapshot snapshot,
        PositionEntityId entityId)
    {
        if (!TryCreateStamp(snapshot, out var stamp, out var invalidStampReason))
        {
            return PositionRuntimeConfigurationLoadResult.InvalidStamp(invalidStampReason);
        }

        if (snapshot.OrganizationId != entityId.Organization)
        {
            return PositionRuntimeConfigurationLoadResult.Incomplete(
                $"Registry snapshot organization '{snapshot.OrganizationId.Value}' does not match requested organization '{entityId.Organization.Value}'.");
        }

        if (snapshot.Organization?.Value is not { } organization
            || organization.Id != entityId.Organization)
        {
            return PositionRuntimeConfigurationLoadResult.Incomplete(
                $"Registry snapshot for organization '{entityId.Organization.Value}' is missing coherent organization metadata.");
        }

        if (snapshot.Positions is null)
        {
            return PositionRuntimeConfigurationLoadResult.Incomplete(
                $"Registry snapshot for organization '{entityId.Organization.Value}' is missing positions.");
        }

        if (!snapshot.Positions.TryGetValue(entityId.Position, out var positionEntry))
        {
            return PositionRuntimeConfigurationLoadResult.Missing(
                $"Position '{entityId.Position.Value}' was not found in organization '{entityId.Organization.Value}'.");
        }

        if (positionEntry?.Value is not { } position
            || position.Id != entityId.Position)
        {
            return PositionRuntimeConfigurationLoadResult.Incomplete(
                $"Registry snapshot for position '{entityId.Value}' is missing coherent position metadata.");
        }

        if (snapshot.Occupants is null
            || !snapshot.Occupants.TryGetValue(entityId.Position, out var occupantEntry)
            || occupantEntry?.Value is not { } occupant
            || occupant.PositionId != entityId.Position)
        {
            return PositionRuntimeConfigurationLoadResult.Incomplete(
                $"Registry snapshot for position '{entityId.Value}' is missing coherent occupant metadata.");
        }

        if (snapshot.Authorities is null
            || !snapshot.Authorities.TryGetValue(entityId.Position, out var authorityEntry)
            || authorityEntry?.Value is not { } authority
            || authority.PositionId != entityId.Position)
        {
            return PositionRuntimeConfigurationLoadResult.Incomplete(
                $"Registry snapshot for position '{entityId.Value}' is missing coherent authority metadata.");
        }

        if (occupant.Subscriptions is null || occupant.Tools is null)
        {
            return PositionRuntimeConfigurationLoadResult.Incomplete(
                $"Registry snapshot for position '{entityId.Value}' has incomplete occupant collections.");
        }

        if (authority.CanDecide is null
            || authority.MustEscalate is null
            || authority.RequiresHumanApproval is null)
        {
            return PositionRuntimeConfigurationLoadResult.Incomplete(
                $"Registry snapshot for position '{entityId.Value}' has incomplete authority collections.");
        }

        if (!TryProjectSchedules(snapshot, entityId, out var schedules, out var scheduleReason))
        {
            return PositionRuntimeConfigurationLoadResult.Incomplete(scheduleReason);
        }

        try
        {
            return PositionRuntimeConfigurationLoadResult.Loaded(
                new PositionRuntimeConfiguration(
                    stamp!,
                    entityId.Organization,
                    entityId.Position,
                    new PositionRuntimeDescriptor(
                        position.Unit,
                        position.ReportsTo,
                        position.Name,
                        position.Timezone),
                    new OccupantRuntimeConfiguration(
                        occupant.Type,
                        occupant.IdentityPromptRef,
                        occupant.Ai,
                        occupant.WorkingHours,
                        occupant.Subscriptions,
                        occupant.Tools),
                    new PositionAuthorityRuntimeConfiguration(
                        authority.CanDecide,
                        authority.MustEscalate,
                        authority.RequiresHumanApproval),
                    schedules));
        }
        catch (ArgumentException exception)
        {
            return PositionRuntimeConfigurationLoadResult.Incomplete(
                $"Registry snapshot for position '{entityId.Value}' is not a complete runtime configuration: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            return PositionRuntimeConfigurationLoadResult.Incomplete(
                $"Registry snapshot for position '{entityId.Value}' is not a complete runtime configuration: {exception.Message}");
        }
    }

    private static bool TryCreateStamp(
        OrganizationRegistrySnapshot snapshot,
        out PositionConfigurationStamp? stamp,
        out string reason)
    {
        try
        {
            stamp = new PositionConfigurationStamp(snapshot.Version, snapshot.Fingerprint);
            reason = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        {
            stamp = null;
            reason = $"Registry snapshot for organization '{snapshot.OrganizationId.Value}' has an invalid configuration stamp: {exception.Message}";
            return false;
        }
    }

    private static bool TryProjectSchedules(
        OrganizationRegistrySnapshot snapshot,
        PositionEntityId entityId,
        out IReadOnlyList<PositionScheduleRuntimeConfiguration> schedules,
        out string reason)
    {
        schedules = Array.Empty<PositionScheduleRuntimeConfiguration>();

        if (snapshot.Schedules is null)
        {
            reason = $"Registry snapshot for organization '{entityId.Organization.Value}' is missing schedules.";
            return false;
        }

        var projected = new List<PositionScheduleRuntimeConfiguration>();
        foreach (var (key, entry) in snapshot.Schedules
            .Where(pair => pair.Key.PositionId == entityId.Position
                || pair.Value?.Value?.PositionId == entityId.Position)
            .OrderBy(pair => pair.Key.ScheduleId, StringComparer.Ordinal))
        {
            if (entry?.Value is not { } schedule)
            {
                reason = $"Registry snapshot for position '{entityId.Value}' has an empty schedule entry.";
                return false;
            }

            if (key.PositionId != entityId.Position
                || schedule.PositionId != entityId.Position
                || !string.Equals(key.ScheduleId, schedule.ScheduleId, StringComparison.Ordinal))
            {
                reason = $"Registry snapshot for position '{entityId.Value}' has incoherent schedule metadata.";
                return false;
            }

            try
            {
                projected.Add(new PositionScheduleRuntimeConfiguration(
                    schedule.ScheduleId,
                    schedule.Cron,
                    schedule.Instruction));
            }
            catch (ArgumentException exception)
            {
                reason = $"Registry snapshot for position '{entityId.Value}' has an invalid schedule: {exception.Message}";
                return false;
            }
        }

        schedules = projected;
        reason = string.Empty;
        return true;
    }
}
