using Hive.Domain.Identity;

namespace Hive.Domain.Organization.Configuration;

/// <summary>
/// A position in the structure (§4.8 <c>positions[]</c>): its <see cref="Id"/>, optional
/// <see cref="Name"/>, the <see cref="Unit"/> it belongs to, its direct superior
/// (<see cref="ReportsTo"/>, <see langword="null"/> only on the root unit leadership), the optional
/// <see cref="Timezone"/> inherited by schedules and working hours (§6.2), and the
/// <see cref="Occupant"/> interior. Cross-references and structural rules are validated later
/// (US-F0-05-T06/T07).
/// </summary>
public sealed record PositionConfiguration
{
    /// <summary>Creates position <paramref name="id"/> in <paramref name="unit"/> filled by <paramref name="occupant"/>.</summary>
    public PositionConfiguration(
        PositionId id,
        UnitId unit,
        OccupantConfiguration occupant,
        PositionId? reportsTo = null,
        string? name = null,
        string? timezone = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(occupant);

        Id = id;
        Unit = unit;
        Occupant = occupant;
        ReportsTo = reportsTo;
        Name = name;
        Timezone = timezone;
    }

    /// <summary>The unique position identifier.</summary>
    public PositionId Id { get; }

    /// <summary>The optional human-readable label of the position.</summary>
    public string? Name { get; }

    /// <summary>The unit the position belongs to.</summary>
    public UnitId Unit { get; }

    /// <summary>The direct organizational superior, or <see langword="null"/> for the root unit leadership.</summary>
    public PositionId? ReportsTo { get; }

    /// <summary>The optional IANA timezone of the position, inherited by schedules and working hours.</summary>
    public string? Timezone { get; }

    /// <summary>The occupant and per-position configuration.</summary>
    public OccupantConfiguration Occupant { get; }
}
