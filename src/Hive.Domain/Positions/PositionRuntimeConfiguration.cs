using System.Collections.Immutable;
using Hive.Domain.Identity;

namespace Hive.Domain.Positions;

/// <summary>
/// Complete runtime configuration required before a position entity may process business commands
/// (US-F0-06-T08a).
/// </summary>
public sealed record PositionRuntimeConfiguration
{
    public PositionRuntimeConfiguration(
        PositionConfigurationStamp stamp,
        OrganizationId organizationId,
        PositionId positionId,
        PositionRuntimeDescriptor position,
        OccupantRuntimeConfiguration occupant,
        PositionAuthorityRuntimeConfiguration authority,
        IEnumerable<PositionScheduleRuntimeConfiguration>? schedules = null)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(positionId);
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(occupant);
        ArgumentNullException.ThrowIfNull(authority);

        Stamp = stamp;
        OrganizationId = organizationId;
        PositionId = positionId;
        Position = position;
        Occupant = occupant;
        Authority = authority;
        Schedules = ToValidatedArray(schedules, nameof(schedules));
    }

    /// <summary>The registry configuration stamp represented by this runtime projection.</summary>
    public PositionConfigurationStamp Stamp { get; }

    /// <summary>The organization that owns the configured position.</summary>
    public OrganizationId OrganizationId { get; }

    /// <summary>The configured position identity.</summary>
    public PositionId PositionId { get; }

    /// <summary>Essential position metadata.</summary>
    public PositionRuntimeDescriptor Position { get; }

    /// <summary>The position occupant runtime configuration.</summary>
    public OccupantRuntimeConfiguration Occupant { get; }

    /// <summary>The position authority runtime configuration.</summary>
    public PositionAuthorityRuntimeConfiguration Authority { get; }

    /// <summary>The proactive schedules relevant to this position.</summary>
    public ImmutableArray<PositionScheduleRuntimeConfiguration> Schedules { get; }

    /// <summary>Returns true when this configuration belongs to the supplied sharded entity id.</summary>
    public bool Matches(PositionEntityId entityId)
    {
        ArgumentNullException.ThrowIfNull(entityId);

        return OrganizationId == entityId.Organization && PositionId == entityId.Position;
    }

    private static ImmutableArray<T> ToValidatedArray<T>(IEnumerable<T>? source, string parameterName)
        where T : class
    {
        if (source is null)
        {
            return ImmutableArray<T>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<T>();
        foreach (var item in source)
        {
            if (item is null)
            {
                throw new ArgumentException("Collection cannot contain null items.", parameterName);
            }

            builder.Add(item);
        }

        return builder.ToImmutable();
    }
}
