using Akka.Actor;
using Akka.Cluster;
using Hive.Infrastructure.Configuration;

namespace Hive.Actors.Gateway;

/// <summary>
/// The node's route to the sharded AI gateway (US-F1-05-T07): the shard region on a
/// <c>gateway</c> node, or the region proxy on an <c>agents</c> node that does not host it.
/// </summary>
/// <remarks>
/// A route is only usable while the cluster actually has a <c>gateway</c> member: in a topology
/// where the role was never separated — or before any gateway node joined — the caller keeps using
/// the in-process gateway, which is the colocated behaviour of a single node and never a silent
/// failure. Once a gateway member is up, every call routes to the entity that owns the provider.
/// </remarks>
public sealed class AiGatewayShardRegion
{
    private IActorRef? _route;
    private Func<bool>? _gatewayMemberProbe;

    /// <summary>The shard region or region proxy, or <see langword="null"/> when none was started.</summary>
    public IActorRef? Route => _route;

    /// <summary>Whether a route was materialized on this node.</summary>
    public bool IsRouted => _route is not null;

    /// <summary>
    /// Whether a call can be routed right now: a materialized route and at least one cluster member
    /// with the <c>gateway</c> role up to host the provider entities.
    /// </summary>
    public bool CanRoute => _route is not null && (_gatewayMemberProbe?.Invoke() ?? false);

    /// <summary>
    /// Publishes the route materialized by a role workload. The first non-null route wins, so an
    /// all-in-one node keeps the region it hosts and never replaces it with a proxy.
    /// </summary>
    public void Publish(ActorSystem system, IActorRef route)
    {
        ArgumentNullException.ThrowIfNull(system);
        Publish(route, () => HasGatewayMember(system));
    }

    /// <summary>Publishes a route with an explicit gateway-membership probe (test seam).</summary>
    internal void Publish(IActorRef route, Func<bool> hasGatewayMember)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(hasGatewayMember);

        if (Interlocked.CompareExchange(ref _route, route, null) is null)
        {
            _gatewayMemberProbe = hasGatewayMember;
        }
    }

    private static bool HasGatewayMember(ActorSystem system) =>
        Cluster.Get(system).State.Members.Any(member =>
            member.Status == MemberStatus.Up
            && member.Roles.Contains(NodeRoleNames.Gateway));
}
