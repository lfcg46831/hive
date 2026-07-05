using Akka.Actor;
using Akka.Cluster.Sharding;
using Hive.Actors.Sharding;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;

namespace Hive.Actors.Scheduling;

public interface ISchedulerPulseDispatcher
{
    Task DeliverAsync(
        IActorContext context,
        Pulse pulse,
        CancellationToken cancellationToken = default);
}

internal sealed class NoopSchedulerPulseDispatcher : ISchedulerPulseDispatcher
{
    public static NoopSchedulerPulseDispatcher Instance { get; } = new();

    private NoopSchedulerPulseDispatcher()
    {
    }

    public Task DeliverAsync(
        IActorContext context,
        Pulse pulse,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pulse);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.CompletedTask;
    }
}

internal sealed class AkkaClusterShardingSchedulerPulseDispatcher : ISchedulerPulseDispatcher
{
    public static AkkaClusterShardingSchedulerPulseDispatcher Instance { get; } = new();

    private AkkaClusterShardingSchedulerPulseDispatcher()
    {
    }

    public Task DeliverAsync(
        IActorContext context,
        Pulse pulse,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pulse);
        cancellationToken.ThrowIfCancellationRequested();

        var region = ClusterSharding.Get(context.System).ShardRegion(PositionEntityId.EntityTypeName);
        region.Tell(ToEnvelope(pulse));
        return Task.CompletedTask;
    }

    internal static PositionEnvelope ToEnvelope(Pulse pulse)
    {
        ArgumentNullException.ThrowIfNull(pulse);

        if (pulse.To is not PositionEndpointRef target)
        {
            throw new InvalidOperationException(
                "Scheduler pulses must target a position endpoint before sharding delivery.");
        }

        return PositionEnvelope.For(
            PositionEntityId.From(pulse.OrganizationId, target.PositionId),
            new AcceptMessage(pulse));
    }
}
