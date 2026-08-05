using Hive.Actors.Inbox;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;
using Hive.Infrastructure.Inbox.ReadModels;

namespace Hive.Tests;

public sealed class InboxProjectionWorkerTests
{
    private static readonly DateTimeOffset OccurredAt =
        new(2026, 8, 4, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Message_received_produces_position_event_and_organizational_message_facts()
    {
        var item = JournalEvent(offset: 7, MessageReceived());

        var facts = InboxProjectionWorker.Facts(item).ToArray();

        Assert.Equal(2, facts.Length);
        var positionEvent = Assert.Single(
            facts,
            fact => fact.Source == InboxProjectionSource.PositionEvent);
        var message = Assert.Single(
            facts,
            fact => fact.Source == InboxProjectionSource.OrganizationalMessage);
        Assert.Equal("message-received", positionEvent.FactType);
        Assert.Equal("memo", message.FactType);
        Assert.Equal(item.PersistenceId, positionEvent.PersistenceId);
        Assert.Equal(item.PersistenceSequence, positionEvent.PersistenceSequence);
        Assert.Equal(item.Event.OccurredAt, positionEvent.OccurredAtUtc);
        Assert.Equal(MessageIdValue, message.MessageId);
        Assert.Equal(ThreadIdValue, message.ThreadId);
        Assert.Contains("\"Body\":\"Status update\"", message.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_message_position_event_produces_only_a_position_event_fact()
    {
        var facts = InboxProjectionWorker.Facts(
            JournalEvent(8, new PositionPassivated(OccurredAt, "idle")));

        var fact = Assert.Single(facts);
        Assert.Equal(InboxProjectionSource.PositionEvent, fact.Source);
        Assert.Equal("position-passivated", fact.FactType);
        Assert.Null(fact.MessageId);
        Assert.Null(fact.ThreadId);
    }

    [Fact]
    public async Task Durable_checkpoint_resumes_after_the_last_captured_journal_offset()
    {
        var events = new[]
        {
            JournalEvent(3, MessageReceived()),
            JournalEvent(5, new PositionPassivated(OccurredAt.AddMinutes(1), "idle")),
            JournalEvent(8, new PositionPassivated(OccurredAt.AddMinutes(2), "idle")),
        };
        var feed = new RecordingFeed();
        var firstJournal = new RecordingJournal(events[..2]);
        var firstWorker = new InboxProjectionWorker(firstJournal, feed, TimeProvider.System);

        var firstCaptured = await firstWorker.CapturePositionJournalBatchAsync(CancellationToken.None);
        var restartedJournal = new RecordingJournal(events);
        var restartedWorker = new InboxProjectionWorker(
            restartedJournal,
            feed,
            TimeProvider.System);
        var restartedCaptured = await restartedWorker.CapturePositionJournalBatchAsync(
            CancellationToken.None);

        Assert.Equal(2, firstCaptured);
        Assert.Equal(1, restartedCaptured);
        Assert.Equal([0], firstJournal.RequestedAfterOffsets);
        Assert.Equal([5], restartedJournal.RequestedAfterOffsets);
        Assert.Equal(8, feed.PositionCheckpoint);
        Assert.Equal([3L, 5L, 8L], feed.CapturedOffsets);
    }

    [Fact]
    public async Task Journal_capture_stops_before_checkpointing_a_failed_fact()
    {
        var events = new[]
        {
            JournalEvent(2, MessageReceived()),
            JournalEvent(4, new PositionPassivated(OccurredAt.AddMinutes(1), "idle")),
        };
        var feed = new RecordingFeed(failAtOffset: 4);
        var worker = new InboxProjectionWorker(
            new RecordingJournal(events),
            feed,
            TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            worker.CapturePositionJournalBatchAsync(CancellationToken.None));

        Assert.Equal(2, feed.PositionCheckpoint);
        Assert.Equal([2L], feed.CapturedOffsets);
    }

    [Fact]
    public async Task Projection_restores_committed_facts_and_suppresses_a_redelivered_item()
    {
        var messageFact = Assert.Single(
            InboxProjectionWorker.Facts(JournalEvent(offset: 1, MessageReceived())),
            fact => fact.Source == InboxProjectionSource.OrganizationalMessage);
        var redeliveredFact = new InboxProjectionFact(
            messageFact.Source,
            sourceOffset: 2,
            messageFact.OrganizationId,
            messageFact.FactType,
            messageFact.OccurredAtUtc,
            messageFact.PayloadJson,
            messageFact.PositionId,
            messageFact.PersistenceId,
            persistenceSequence: 2,
            messageFact.MessageId,
            messageFact.ThreadId);
        var items = new[]
        {
            new InboxProjectionFactItem(1, messageFact),
            new InboxProjectionFactItem(2, redeliveredFact),
        };
        var feed = new RecordingFeed(
            projectionFacts: items,
            initialProjectionSequence: 1);
        var worker = new InboxProjectionWorker(
            new RecordingJournal([]),
            feed,
            new FixedTimeProvider(OccurredAt));

        var applied = await worker.ApplyProjectionBatchAsync(CancellationToken.None);
        var repeated = await worker.ApplyProjectionBatchAsync(CancellationToken.None);

        Assert.Equal(1, applied);
        Assert.Equal(0, repeated);
        Assert.Equal([2L], feed.AppliedProjectionSequences);
        Assert.Empty(feed.ProjectionChanges);
        Assert.Equal(2, feed.ProjectionSequence);
        Assert.Equal(OccurredAt, feed.LastEventAppliedAtUtc);
    }

    private static InboxProjectionJournalEvent JournalEvent(
        long offset,
        PositionEvent @event) =>
        new(
            offset,
            "position:acme/delivery-lead",
            offset,
            PositionEntityId.Parse("acme/delivery-lead"),
            @event);

    private static MessageReceived MessageReceived() =>
        new(
            new Memo(
                MessageIdValue,
                OrganizationId.From("acme"),
                new PositionEndpointRef(PositionId.From("engineer")),
                new PositionEndpointRef(PositionId.From("delivery-lead")),
                ThreadIdValue,
                Priority.Normal,
                schemaVersion: 1,
                OccurredAt.AddMinutes(-1),
                deadline: null,
                "Status update"),
            OccurredAt);

    private static MessageId MessageIdValue { get; } =
        MessageId.From(Guid.Parse("60887ac0-c892-4554-827d-36730a8ec299"));

    private static ThreadId ThreadIdValue { get; } =
        ThreadId.From(Guid.Parse("8eb80f58-d7ed-4e9e-844e-0e9a176693f8"));

    private sealed class RecordingJournal(
        IReadOnlyCollection<InboxProjectionJournalEvent> events)
        : IInboxProjectionJournal
    {
        public List<long> RequestedAfterOffsets { get; } = [];

        public Task<IReadOnlyList<InboxProjectionJournalEvent>> ReadBatchAsync(
            long afterOffset,
            int batchSize,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedAfterOffsets.Add(afterOffset);
            IReadOnlyList<InboxProjectionJournalEvent> result = events
                .Where(item => item.Offset > afterOffset)
                .OrderBy(item => item.Offset)
                .Take(batchSize)
                .ToArray();
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingFeed(
        long? failAtOffset = null,
        IReadOnlyCollection<InboxProjectionFactItem>? projectionFacts = null,
        long initialProjectionSequence = 0) : IInboxProjectionFeed
    {
        private readonly IReadOnlyCollection<InboxProjectionFactItem> _projectionFacts =
            projectionFacts ?? [];

        public bool IsConfigured => true;

        public long PositionCheckpoint { get; private set; }

        public List<long> CapturedOffsets { get; } = [];

        public long ProjectionSequence { get; private set; } = initialProjectionSequence;

        public DateTimeOffset? LastEventAppliedAtUtc { get; private set; } =
            projectionFacts?
                .SingleOrDefault(item => item.SequenceId == initialProjectionSequence)?
                .Fact.OccurredAtUtc;

        public List<long> AppliedProjectionSequences { get; } = [];

        public List<InboxProjectionChange> ProjectionChanges { get; } = [];

        public ValueTask<long> ReadCheckpointAsync(
            InboxProjectionSubscription subscription,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(subscription switch
            {
                InboxProjectionSubscription.PositionJournal => PositionCheckpoint,
                InboxProjectionSubscription.AuditLog => 0,
                _ => throw new ArgumentOutOfRangeException(nameof(subscription)),
            });
        }

        public ValueTask<bool> CapturePositionJournalAsync(
            long sourceOffset,
            IReadOnlyCollection<InboxProjectionFact> facts,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sourceOffset == failAtOffset)
            {
                throw new InvalidOperationException("forced capture failure");
            }

            Assert.NotEmpty(facts);
            Assert.All(facts, fact => Assert.Equal(sourceOffset, fact.SourceOffset));
            if (sourceOffset <= PositionCheckpoint)
            {
                return ValueTask.FromResult(false);
            }

            PositionCheckpoint = sourceOffset;
            CapturedOffsets.Add(sourceOffset);
            return ValueTask.FromResult(true);
        }

        public ValueTask<int> CaptureAuditLogBatchAsync(
            int batchSize,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(0);
        }

        public ValueTask<InboxProjectionProgress> ReadProjectionProgressAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new InboxProjectionProgress(
                ProjectionSequence,
                LastEventAppliedAtUtc));
        }

        public ValueTask<IReadOnlyList<InboxProjectionFactItem>> ReadProjectionFactsAsync(
            long afterSequenceId,
            int batchSize,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<InboxProjectionFactItem> result = _projectionFacts
                .Where(item => item.SequenceId > afterSequenceId)
                .OrderBy(item => item.SequenceId)
                .Take(batchSize)
                .ToArray();
            return ValueTask.FromResult(result);
        }

        public ValueTask<bool> ApplyProjectionFactAsync(
            InboxProjectionFactItem item,
            IReadOnlyCollection<InboxProjectionChange> changes,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.SequenceId <= ProjectionSequence)
            {
                return ValueTask.FromResult(false);
            }

            var next = _projectionFacts
                .Where(candidate => candidate.SequenceId > ProjectionSequence)
                .Min(candidate => candidate.SequenceId);
            Assert.Equal(next, item.SequenceId);
            ProjectionSequence = item.SequenceId;
            LastEventAppliedAtUtc = item.Fact.OccurredAtUtc;
            AppliedProjectionSequences.Add(item.SequenceId);
            ProjectionChanges.AddRange(changes);
            return ValueTask.FromResult(true);
        }

        public ValueTask<int> ApplyProjectionChangesAsync(
            long expectedProjectionSequence,
            IReadOnlyCollection<InboxProjectionChange> changes,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(ProjectionSequence, expectedProjectionSequence);
            ProjectionChanges.AddRange(changes);
            return ValueTask.FromResult(changes.Count);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
