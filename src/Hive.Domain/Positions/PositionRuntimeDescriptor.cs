using System.Collections.Immutable;
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
        string? timezone = null,
        IEnumerable<PositionId>? directSubordinates = null)
    {
        ArgumentNullException.ThrowIfNull(unit);

        Unit = unit;
        ReportsTo = reportsTo;
        Name = name is null ? null : CommandText.RequireContent(name, nameof(name));
        Timezone = timezone is null ? null : CommandText.RequireContent(timezone, nameof(timezone));
        DirectSubordinates = ToValidatedSubordinates(
            directSubordinates,
            nameof(directSubordinates));
    }

    /// <summary>The organizational unit that owns the position.</summary>
    public UnitId Unit { get; }

    /// <summary>The direct superior position, or null for root leadership.</summary>
    public PositionId? ReportsTo { get; }

    /// <summary>The optional human-readable position label.</summary>
    public string? Name { get; }

    /// <summary>The optional IANA timezone used by schedules and working hours.</summary>
    public string? Timezone { get; }

    /// <summary>The positions that report directly to this position and may receive child directives.</summary>
    public ImmutableArray<PositionId> DirectSubordinates { get; }

    private static ImmutableArray<PositionId> ToValidatedSubordinates(
        IEnumerable<PositionId>? source,
        string parameterName)
    {
        if (source is null)
        {
            return ImmutableArray<PositionId>.Empty;
        }

        var snapshot = source.ToArray();
        foreach (var subordinate in snapshot)
        {
            ArgumentNullException.ThrowIfNull(subordinate, parameterName);
        }

        var builder = ImmutableArray.CreateBuilder<PositionId>();
        var seen = new HashSet<PositionId>();
        foreach (var subordinate in snapshot.OrderBy(position => position.Value, StringComparer.Ordinal))
        {
            if (!seen.Add(subordinate))
            {
                throw new ArgumentException(
                    $"Direct subordinate '{subordinate.Value}' was supplied more than once.",
                    parameterName);
            }

            builder.Add(subordinate);
        }

        return builder.ToImmutable();
    }
}
