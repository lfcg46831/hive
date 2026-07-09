using Akka.Actor;
using Akka.Cluster;
using Akka.Cluster.Sharding;
using Hive.Actors.Sharding;
using Hive.Domain.Identity;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Hive.Api.Directives;

internal interface IPositionCommandDispatcher
{
    ValueTask DispatchAsync(
        PositionEnvelope envelope,
        CancellationToken cancellationToken);
}

internal sealed class AkkaClusterShardingPositionCommandDispatcher : IPositionCommandDispatcher
{
    private readonly ActorSystem _system;
    private readonly int _numberOfShards;
    private readonly object _regionGate = new();
    private IActorRef? _region;

    public AkkaClusterShardingPositionCommandDispatcher(
        ActorSystem system,
        IOptions<HiveOptions> options)
        : this(system, ResolveNumberOfShards(options))
    {
    }

    internal AkkaClusterShardingPositionCommandDispatcher(
        ActorSystem system,
        int numberOfShards = PositionMessageExtractor.DefaultNumberOfShards)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _numberOfShards = numberOfShards;
    }

    public ValueTask DispatchAsync(
        PositionEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();

        var region = GetOrStartShardRegion();
        region.Tell(envelope);
        return ValueTask.CompletedTask;
    }

    private IActorRef GetOrStartShardRegion()
    {
        if (_region is { } existing)
        {
            return existing;
        }

        lock (_regionGate)
        {
            if (_region is { } cached)
            {
                return cached;
            }

            var sharding = ClusterSharding.Get(_system);
            try
            {
                _region = sharding.ShardRegion(PositionEntityId.EntityTypeName);
            }
            catch (ArgumentException) when (!Cluster.Get(_system).SelfRoles.Contains(NodeRoleNames.Agents))
            {
                _region = sharding.StartProxy(
                    PositionEntityId.EntityTypeName,
                    NodeRoleNames.Agents,
                    new PositionMessageExtractor(_numberOfShards));
            }

            return _region;
        }
    }

    private static int ResolveNumberOfShards(IOptions<HiveOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.Value.Agents?.NumberOfShards
            ?? PositionMessageExtractor.DefaultNumberOfShards;
    }
}
