using Akka.Persistence.Query;
using Hive.Actors.Inbox;
using Hive.Actors.Positions;
using Hive.Domain.Positions;

namespace Hive.Tests;

public sealed class ProjectionJournalMappingTests
{
    private static readonly DateTimeOffset OccurredAt =
        new(2026, 8, 9, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Sharding_events_are_ignored_while_their_offsets_remain_observable()
    {
        var positionEvent = new PositionPassivated(OccurredAt, "idle");
        var envelopes = new[]
        {
            Envelope(1, "/system/sharding/positionCoordinator/singleton/coordinator", new object()),
            Envelope(2, "position:acme/delivery-lead", positionEvent),
            Envelope(3, "akka.cluster.sharding.positionCoordinator", new object()),
        };

        var inbox = AkkaInboxProjectionJournal.MapBatch(afterOffset: 0, envelopes);
        var liveState = AkkaPositionLiveStateProjectionJournal.MapBatch(
            afterOffset: 0,
            envelopes);

        Assert.Equal(3, inbox.LastObservedOffset);
        Assert.Equal(2, Assert.Single(inbox.Events).Offset);
        Assert.Same(positionEvent, inbox.Events[0].Event);
        Assert.Equal(3, liveState.LastObservedOffset);
        Assert.Equal(2, Assert.Single(liveState.Events).Offset);
        Assert.Same(positionEvent, liveState.Events[0].Event);
    }

    [Fact]
    public void Non_position_payload_under_a_position_persistence_id_still_fails_closed()
    {
        var envelope = Envelope(1, "position:acme/delivery-lead", new object());

        Assert.Throws<InvalidOperationException>(() =>
            AkkaInboxProjectionJournal.MapBatch(afterOffset: 0, [envelope]));
        Assert.Throws<InvalidOperationException>(() =>
            AkkaPositionLiveStateProjectionJournal.MapBatch(afterOffset: 0, [envelope]));
    }

    private static EventEnvelope Envelope(
        long offset,
        string persistenceId,
        object @event)
    {
#pragma warning disable CS0618 // Akka exposes no non-obsolete public test constructor.
        return new(Offset.Sequence(offset), persistenceId, offset, @event, OccurredAt.UtcTicks);
#pragma warning restore CS0618
    }
}
