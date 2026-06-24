using Akka.Actor;

namespace Hive.Actors.Sharding;

/// <summary>
/// Default <see cref="IPositionEntityProps"/> used until the real persistent <c>PositionActor</c>
/// lands (US-F0-06-T06b/T09). It spawns a no-op entity so the shard region can be initialized and
/// addressed end-to-end; the entity simply ignores messages for now. Later stories replace this
/// registration with the real entity Props without touching the shard-region wiring of T04b.
/// </summary>
internal sealed class PlaceholderPositionEntityProps : IPositionEntityProps
{
    public Props Create(string entityId) => Props.Create(() => new PlaceholderPositionActor());

    private sealed class PlaceholderPositionActor : UntypedActor
    {
        protected override void OnReceive(object message)
        {
            // No behaviour yet: the persistent PositionActor is implemented in later stories
            // (US-F0-06-T06b/T09). Messages are swallowed quietly so the placeholder entity
            // produces no dead letters while the shard region is exercised end-to-end.
        }
    }
}
