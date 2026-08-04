using Akka.Actor;
using Akka.Persistence.Query;
using Akka.Persistence.Sql.Query;
using Akka.Streams;
using Akka.Streams.Dsl;
using Hive.Actors.Positions;
using Hive.Domain.Identity;
using Hive.Domain.Positions;

namespace Hive.Actors.Inbox;

internal interface IInboxProjectionJournal
{
    Task<IReadOnlyList<InboxProjectionJournalEvent>> ReadBatchAsync(
        long afterOffset,
        int batchSize,
        CancellationToken cancellationToken);
}

internal sealed record InboxProjectionJournalEvent
{
    public InboxProjectionJournalEvent(
        long offset,
        string persistenceId,
        long persistenceSequence,
        PositionEntityId entityId,
        PositionEvent @event)
    {
        if (offset <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Journal offset must be positive.");
        }

        if (string.IsNullOrWhiteSpace(persistenceId))
        {
            throw new ArgumentException(
                "Persistence identifier cannot be empty or whitespace.",
                nameof(persistenceId));
        }

        if (persistenceSequence <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(persistenceSequence),
                persistenceSequence,
                "Persistence sequence must be positive.");
        }

        Offset = offset;
        PersistenceId = persistenceId;
        PersistenceSequence = persistenceSequence;
        EntityId = entityId ?? throw new ArgumentNullException(nameof(entityId));
        Event = @event ?? throw new ArgumentNullException(nameof(@event));
    }

    public long Offset { get; }

    public string PersistenceId { get; }

    public long PersistenceSequence { get; }

    public PositionEntityId EntityId { get; }

    public PositionEvent Event { get; }
}

internal sealed class AkkaInboxProjectionJournal : IInboxProjectionJournal, IDisposable
{
    private readonly ActorSystem _actorSystem;
    private SqlReadJournal? _readJournal;
    private ActorMaterializer? _materializer;

    public AkkaInboxProjectionJournal(ActorSystem actorSystem)
    {
        _actorSystem = actorSystem ?? throw new ArgumentNullException(nameof(actorSystem));
    }

    public async Task<IReadOnlyList<InboxProjectionJournalEvent>> ReadBatchAsync(
        long afterOffset,
        int batchSize,
        CancellationToken cancellationToken)
    {
        if (afterOffset < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(afterOffset),
                afterOffset,
                "Journal checkpoint cannot be negative.");
        }

        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(batchSize),
                batchSize,
                "Journal batch size must be positive.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();
        var envelopes = await _readJournal!
            .CurrentAllEvents(Offset.Sequence(afterOffset))
            .Take(batchSize)
            .RunWith(Sink.Seq<EventEnvelope>(), _materializer!)
            .WaitAsync(cancellationToken);

        return envelopes.Select(Map).ToArray();
    }

    public void Dispose()
    {
        if (_materializer is { IsShutdown: false })
        {
            _materializer.Shutdown();
        }
    }

    private void EnsureInitialized()
    {
        if (_readJournal is not null)
        {
            return;
        }

        _readJournal = PersistenceQuery.Get(_actorSystem)
            .ReadJournalFor<SqlReadJournal>(SqlReadJournal.Identifier);
        _materializer = ActorMaterializer.Create(
            _actorSystem,
            namePrefix: "inbox-projection");
    }

    private static InboxProjectionJournalEvent Map(EventEnvelope envelope)
    {
        if (envelope.Offset is not Sequence sequence || sequence.Value <= 0)
        {
            throw new InvalidOperationException(
                $"Inbox journal event '{envelope.PersistenceId}' has no positive sequence offset.");
        }

        if (!envelope.PersistenceId.StartsWith(PositionActor.PersistenceIdPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Persistence id '{envelope.PersistenceId}' does not belong to a PositionActor.");
        }

        if (envelope.Event is not PositionEvent positionEvent)
        {
            throw new InvalidOperationException(
                $"Inbox journal entry '{envelope.PersistenceId}/{envelope.SequenceNr}' is not a PositionEvent.");
        }

        var entityId = PositionEntityId.Parse(
            envelope.PersistenceId[PositionActor.PersistenceIdPrefix.Length..]);
        return new InboxProjectionJournalEvent(
            sequence.Value,
            envelope.PersistenceId,
            envelope.SequenceNr,
            entityId,
            positionEvent);
    }
}
