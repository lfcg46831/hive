using Akka.Actor;
using Akka.Cluster.Sharding;
using Hive.Actors.Sharding;
using Hive.Domain.Identity;

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

    public AkkaClusterShardingPositionCommandDispatcher(ActorSystem system)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
    }

    public ValueTask DispatchAsync(
        PositionEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();

        var region = ClusterSharding.Get(_system).ShardRegion(PositionEntityId.EntityTypeName);
        region.Tell(envelope);
        return ValueTask.CompletedTask;
    }
}
