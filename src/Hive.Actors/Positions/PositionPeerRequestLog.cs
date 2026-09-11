using Akka.Actor;
using Akka.Cluster.Sharding;
using Hive.Actors.Sharding;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;

namespace Hive.Actors.Positions;

/// <summary>Queries the durable requester entity, including after passivation or relocation.</summary>
public sealed class PositionPeerRequestLog : IPeerRequestLog
{
    private readonly Func<IActorRef> _region;
    private readonly TimeSpan _timeout;

    public PositionPeerRequestLog(ActorSystem system, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(system);
        _region = () => ClusterSharding.Get(system).ShardRegion(PositionEntityId.EntityTypeName);
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    internal PositionPeerRequestLog(IActorRef region, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(region);
        _region = () => region;
        _timeout = timeout;
    }

    public async ValueTask<PeerRequestRecord?> FindRequestAsync(
        OrganizationId organizationId,
        PositionId requester,
        MessageId requestId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(requester);
        ArgumentNullException.ThrowIfNull(requestId);
        cancellationToken.ThrowIfCancellationRequested();

        var result = await _region().Ask<PeerRequestLookupResult>(
            PositionEnvelope.For(PositionEntityId.From(organizationId, requester),
                new FindPeerRequest(requestId)), _timeout, cancellationToken);
        return result.Record;
    }
}
