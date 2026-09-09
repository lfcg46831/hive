using System.Collections.Immutable;
using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;

namespace Hive.Domain.Organization;

/// <summary>Immutable declared channels and unit membership from one organization registry version.</summary>
public sealed class PeerChannelContractsSnapshot
{
    private readonly ImmutableHashSet<UnitId> _units;
    private readonly ImmutableDictionary<(UnitId From, UnitId To), PeerChannelConfiguration> _channels;

    private PeerChannelContractsSnapshot(
        OrganizationId organizationId,
        ImmutableHashSet<UnitId> units,
        ImmutableDictionary<(UnitId From, UnitId To), PeerChannelConfiguration> channels)
    {
        OrganizationId = organizationId;
        _units = units;
        _channels = channels;
    }

    public OrganizationId OrganizationId { get; }

    public static Builder CreateBuilder(OrganizationId organizationId)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        return new(organizationId);
    }

    /// <summary>Looks up only declared channels; absence requires two known units.</summary>
    public PeerChannelConfiguration? Resolve(UnitId fromUnitId, UnitId toUnitId)
    {
        ArgumentNullException.ThrowIfNull(fromUnitId);
        ArgumentNullException.ThrowIfNull(toUnitId);
        foreach (var unitId in new[] { fromUnitId, toUnitId })
        {
            if (!_units.Contains(unitId))
                throw PeerChannelContractNotFoundException.ForUnit(OrganizationId, unitId);
        }

        return _channels.GetValueOrDefault((fromUnitId, toUnitId));
    }

    public sealed class Builder
    {
        private readonly OrganizationId _organizationId;
        private readonly Dictionary<UnitId, ImmutableArray<PeerChannelConfiguration>> _units = new();

        internal Builder(OrganizationId organizationId) => _organizationId = organizationId;

        /// <summary>Adds a unit and its incoming contracts; source references are checked at build time.</summary>
        public Builder AddUnit(UnitId unitId, IEnumerable<PeerChannelConfiguration> incomingChannels)
        {
            ArgumentNullException.ThrowIfNull(unitId);
            ArgumentNullException.ThrowIfNull(incomingChannels);
            var channels = incomingChannels.ToImmutableArray();
            var sources = new HashSet<UnitId>();
            foreach (var channel in channels)
            {
                ArgumentNullException.ThrowIfNull(channel);
                if (channel.From == unitId || !sources.Add(channel.From))
                    throw new ArgumentException("Incoming channels must have unique sources distinct from the destination.", nameof(incomingChannels));
            }

            if (!_units.TryAdd(unitId, channels))
                throw new ArgumentException("The unit was already added to the snapshot.", nameof(unitId));
            return this;
        }

        public PeerChannelContractsSnapshot Build()
        {
            var channels = ImmutableDictionary.CreateBuilder<(UnitId From, UnitId To), PeerChannelConfiguration>();
            foreach (var (destination, incoming) in _units)
            {
                foreach (var channel in incoming)
                {
                    if (!_units.ContainsKey(channel.From))
                        throw new InvalidOperationException($"Channel source unit '{channel.From.Value}' is not declared in the snapshot.");
                    // Canonical type order makes equivalent declarations resolve identically.
                    channels.Add((channel.From, destination), new PeerChannelConfiguration(
                        channel.From, channel.Types.Order().ToArray(), channel.MaxOpenRequests, channel.OnRejection));
                }
            }

            return new(_organizationId, _units.Keys.ToImmutableHashSet(), channels.ToImmutable());
        }
    }
}
