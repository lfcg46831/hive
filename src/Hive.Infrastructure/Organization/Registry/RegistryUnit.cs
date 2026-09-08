using System.Collections.Immutable;
using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;

namespace Hive.Infrastructure.Organization.Registry;

public sealed record RegistryUnit(
    UnitId Id,
    string? Name,
    UnitId? Parent,
    PositionId Leadership)
{
    public RegistryUnit(
        UnitId id,
        string? name,
        UnitId? parent,
        PositionId leadership,
        IReadOnlyList<PeerChannelConfiguration> allowedPeerChannels)
        : this(id, name, parent, leadership)
    {
        ArgumentNullException.ThrowIfNull(allowedPeerChannels);
        AllowedPeerChannels = allowedPeerChannels.ToImmutableArray();
        if (AllowedPeerChannels.Any(channel => channel is null))
        {
            throw new ArgumentException("Collection cannot contain null entries.", nameof(allowedPeerChannels));
        }
    }

    public IReadOnlyList<PeerChannelConfiguration> AllowedPeerChannels { get; } = [];
}
