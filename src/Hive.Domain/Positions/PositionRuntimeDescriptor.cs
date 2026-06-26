using Hive.Domain.Identity;

namespace Hive.Domain.Positions;

/// <summary>
/// Essential runtime data for a configured position, excluding the entity identity carried by
/// <see cref="PositionRuntimeConfiguration"/> (US-F0-06-T08a).
/// </summary>
public sealed record PositionRuntimeDescriptor
{
    public PositionRuntimeDescriptor(
        UnitId unit,
        PositionId? reportsTo = null,
        string? name = null,
        string? timezone = null)
    {
        ArgumentNullException.ThrowIfNull(unit);

        Unit = unit;
        ReportsTo = reportsTo;
        Name = name is null ? null : CommandText.RequireContent(name, nameof(name));
        Timezone = timezone is null ? null : CommandText.RequireContent(timezone, nameof(timezone));
    }

    /// <summary>The organizational unit that owns the position.</summary>
    public UnitId Unit { get; }

    /// <summary>The direct superior position, or null for root leadership.</summary>
    public PositionId? ReportsTo { get; }

    /// <summary>The optional human-readable position label.</summary>
    public string? Name { get; }

    /// <summary>The optional IANA timezone used by schedules and working hours.</summary>
    public string? Timezone { get; }
}
