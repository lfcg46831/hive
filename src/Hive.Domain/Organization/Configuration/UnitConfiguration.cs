using System.Collections.Immutable;
using Hive.Domain.Identity;

namespace Hive.Domain.Organization.Configuration;

/// <summary>
/// A unit of the organizational tree (§4.8 <c>units[]</c>): its <see cref="Id"/>, optional
/// <see cref="Name"/>, the <see cref="Parent"/> unit (<see langword="null"/> only on the root unit)
/// and the <see cref="Leadership"/> position that commands it. The single-leadership-per-unit,
/// acyclicity and reference rules are enforced by US-F0-05-T06/T07, not here.
/// </summary>
public sealed record UnitConfiguration
{
    /// <summary>Creates unit <paramref name="id"/> led by <paramref name="leadership"/>.</summary>
    public UnitConfiguration(
        UnitId id,
        PositionId leadership,
        UnitId? parent = null,
        string? name = null,
        IReadOnlyList<PeerChannelConfiguration>? allowedPeerChannels = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(leadership);

        Id = id;
        Leadership = leadership;
        Parent = parent;
        Name = name;
        AllowedPeerChannels = (allowedPeerChannels ?? []).ToImmutableArray();
        if (AllowedPeerChannels.Any(channel => channel is null))
        {
            throw new ArgumentException("Collection cannot contain null entries.", nameof(allowedPeerChannels));
        }
    }

    /// <summary>The unique unit identifier.</summary>
    public UnitId Id { get; }

    /// <summary>The optional human-readable label of the unit.</summary>
    public string? Name { get; }

    /// <summary>The parent unit, or <see langword="null"/> for the root unit.</summary>
    public UnitId? Parent { get; }

    /// <summary>The position that leads the unit (exactly one per unit, enforced later).</summary>
    public PositionId Leadership { get; }

    /// <summary>Incoming contracts from other units; omitted configuration exposes no channels.</summary>
    public IReadOnlyList<PeerChannelConfiguration> AllowedPeerChannels { get; }
}
