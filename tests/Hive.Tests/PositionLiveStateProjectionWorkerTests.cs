using Hive.Actors.Positions;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;
using Hive.Infrastructure.Organization.ReadModels;

namespace Hive.Tests;

public sealed class PositionLiveStateProjectionWorkerTests
{
    private static readonly DateTimeOffset OccurredAt =
        new(2026, 8, 3, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Message_received_produces_a_position_event_and_an_organizational_message_fact()
    {
        var item = JournalEvent(offset: 7, MessageReceived());

        var facts = PositionLiveStateProjectionWorker.Facts(item).ToArray();

        Assert.Equal(2, facts.Length);
        var positionEvent = Assert.Single(
            facts,
            fact => fact.Source == PositionLiveStateProjectionSource.PositionEvent);
        var message = Assert.Single(
            facts,
            fact => fact.Source == PositionLiveStateProjectionSource.OrganizationalMessage);
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
    public async Task Durable_checkpoint_resumes_after_the_last_captured_journal_offset()
    {
        var taskId = PositionTaskId.From(
            Guid.Parse("d6f3218a-b8a0-42b5-9208-a949e94f03db"));
        var events = new[]
        {
            JournalEvent(3, MessageReceived()),
            JournalEvent(5, new TaskCreated(
                taskId,
                ThreadIdValue,
                "Triage regression",
                Priority.High,
                OccurredAt.AddMinutes(1))),
            JournalEvent(8, new TaskCompleted(
                taskId,
                OccurredAt.AddMinutes(2),
                "Resolved")),
        };
        var feed = new RecordingFeed();
        var firstJournal = new RecordingJournal(events[..2], [1, 4, 6]);
        var firstWorker = new PositionLiveStateProjectionWorker(firstJournal, feed);

        var firstCaptured = await firstWorker.CapturePositionJournalBatchAsync(CancellationToken.None);
        var restartedJournal = new RecordingJournal(events, [1, 4, 6, 9]);
        var restartedWorker = new PositionLiveStateProjectionWorker(restartedJournal, feed);
        var restartedCaptured = await restartedWorker.CapturePositionJournalBatchAsync(
            CancellationToken.None);
        var applied = await restartedWorker.ApplyProjectionBatchAsync(CancellationToken.None);

        Assert.Equal(2, firstCaptured);
        Assert.Equal(1, restartedCaptured);
        Assert.Equal([0], firstJournal.RequestedAfterOffsets);
        Assert.Equal([6], restartedJournal.RequestedAfterOffsets);
        Assert.Equal(9, feed.PositionCheckpoint);
        Assert.Equal([3L, 5L, 8L], feed.CapturedOffsets);
        Assert.Equal([6L, 9L], feed.AdvancedOffsets);
        Assert.True(applied > 0);
        Assert.NotEmpty(feed.ProjectionUpdates);
        Assert.Equal(OccurredAt.AddMinutes(2), feed.LastEventAppliedAtUtc);
    }

    [Fact]
    public async Task Journal_batch_with_only_ignored_events_advances_checkpoint_once()
    {
        var feed = new RecordingFeed();
        var journal = new RecordingJournal([], [1, 2, 3]);
        var worker = new PositionLiveStateProjectionWorker(journal, feed);

        Assert.Equal(0, await worker.CapturePositionJournalBatchAsync(CancellationToken.None));
        Assert.Equal(3, feed.PositionCheckpoint);
        Assert.Equal([3L], feed.AdvancedOffsets);

        var restarted = new PositionLiveStateProjectionWorker(journal, feed);
        Assert.Equal(0, await restarted.CapturePositionJournalBatchAsync(CancellationToken.None));
        Assert.Equal([0L, 3L], journal.RequestedAfterOffsets);
        Assert.Equal([3L], feed.AdvancedOffsets);
    }

    [Fact]
    public async Task Journal_capture_is_sequential_and_stops_before_checkpointing_a_failed_fact()
    {
        var events = new[]
        {
            JournalEvent(2, MessageReceived()),
            JournalEvent(4, new PositionPassivated(OccurredAt.AddMinutes(1), "idle")),
        };
        var feed = new RecordingFeed(failAtOffset: 4);
        var worker = new PositionLiveStateProjectionWorker(new RecordingJournal(events), feed);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            worker.CapturePositionJournalBatchAsync(CancellationToken.None));

        Assert.Equal(2, feed.PositionCheckpoint);
        Assert.Equal([2L], feed.CapturedOffsets);
    }

    [Fact]
    public async Task Projection_replays_committed_facts_and_suppresses_a_redelivered_transition()
    {
        var taskId = PositionTaskId.From(
            Guid.Parse("f84ad918-ec40-45d7-b011-8035f4d19ba6"));
        var created = new TaskCreated(
            taskId,
            ThreadIdValue,
            "Triage regression",
            Priority.High,
            OccurredAt);
        var items = new[]
        {
            ProjectionItem(1, created),
            ProjectionItem(2, created),
            ProjectionItem(3, new TaskCompleted(
                taskId,
                OccurredAt.AddMinutes(2),
                "Resolved")),
        };
        var feed = new RecordingFeed(
            projectionFacts: items,
            initialProjectionSequence: 1);
        var worker = new PositionLiveStateProjectionWorker(
            new RecordingJournal([]),
            feed);

        var applied = await worker.ApplyProjectionBatchAsync(CancellationToken.None);
        var repeated = await worker.ApplyProjectionBatchAsync(CancellationToken.None);

        Assert.Equal(2, applied);
        Assert.Equal(0, repeated);
        Assert.Equal([2L, 3L], feed.AppliedProjectionSequences);
        var update = Assert.Single(feed.ProjectionUpdates);
        Assert.Equal(PositionLiveState.Idle, update.State);
        Assert.Equal(OccurredAt.AddMinutes(2), update.UpdatedAtUtc);
        Assert.Equal(3, feed.ProjectionSequence);
        Assert.Equal(OccurredAt.AddMinutes(2), feed.LastEventAppliedAtUtc);
    }

    private static PositionLiveStateProjectionJournalEvent JournalEvent(
        long offset,
        PositionEvent @event) =>
        new(
            offset,
            "position:acme/delivery-lead",
            offset,
            PositionEntityId.Parse("acme/delivery-lead"),
            @event);

    private static PositionLiveStateProjectionItem ProjectionItem(
        long sequenceId,
        PositionEvent @event) =>
        new(
            sequenceId,
            Assert.Single(
                PositionLiveStateProjectionWorker.Facts(JournalEvent(sequenceId, @event)),
                fact => fact.Source == PositionLiveStateProjectionSource.PositionEvent));

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
        IReadOnlyCollection<PositionLiveStateProjectionJournalEvent> events,
        IReadOnlyCollection<long>? ignoredOffsets = null)
        : IPositionLiveStateProjectionJournal
    {
        public List<long> RequestedAfterOffsets { get; } = [];

        public Task<PositionLiveStateProjectionJournalBatch> ReadBatchAsync(
            long afterOffset,
            int batchSize,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedAfterOffsets.Add(afterOffset);
            var observedOffsets = events
                .Select(item => item.Offset)
                .Concat(ignoredOffsets ?? [])
                .Where(offset => offset > afterOffset)
                .Order()
                .Take(batchSize)
                .ToArray();
            IReadOnlyList<PositionLiveStateProjectionJournalEvent> result = events
                .Where(item => observedOffsets.Contains(item.Offset))
                .OrderBy(item => item.Offset)
                .ToArray();
            return Task.FromResult(new PositionLiveStateProjectionJournalBatch(
                observedOffsets.LastOrDefault(afterOffset),
                result));
        }
    }

    private sealed class RecordingFeed(
        long? failAtOffset = null,
        IReadOnlyCollection<PositionLiveStateProjectionItem>? projectionFacts = null,
        long initialProjectionSequence = 0) : IPositionLiveStateProjectionFeed
    {
        private readonly List<PositionLiveStateProjectionItem> _projectionFacts =
            projectionFacts?.ToList() ?? [];

        public bool IsConfigured => true;

        public long PositionCheckpoint { get; private set; }

        public List<long> CapturedOffsets { get; } = [];

        public List<long> AdvancedOffsets { get; } = [];

        public long ProjectionSequence { get; private set; } = initialProjectionSequence;

        public DateTimeOffset? LastEventAppliedAtUtc { get; private set; } =
            projectionFacts?
                .SingleOrDefault(item => item.SequenceId == initialProjectionSequence)?
                .Fact.OccurredAtUtc;

        public List<long> AppliedProjectionSequences { get; } = [];

        public List<PositionLiveStateProjectionUpdate> ProjectionUpdates { get; } = [];

        public ValueTask<long> ReadCheckpointAsync(
            PositionLiveStateProjectionSubscription subscription,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(subscription switch
            {
                PositionLiveStateProjectionSubscription.PositionJournal => PositionCheckpoint,
                PositionLiveStateProjectionSubscription.AuditLog => 0,
                _ => throw new ArgumentOutOfRangeException(nameof(subscription)),
            });
        }

        public ValueTask<bool> CapturePositionJournalAsync(
            long sourceOffset,
            IReadOnlyCollection<PositionLiveStateProjectionFact> facts,
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
            var sequenceId = _projectionFacts.Count == 0
                ? 0
                : _projectionFacts.Max(item => item.SequenceId);
            _projectionFacts.AddRange(facts.Select(fact =>
                new PositionLiveStateProjectionItem(++sequenceId, fact)));
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> AdvancePositionJournalCheckpointAsync(
            long sourceOffset,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sourceOffset <= PositionCheckpoint)
            {
                return ValueTask.FromResult(false);
            }

            PositionCheckpoint = sourceOffset;
            AdvancedOffsets.Add(sourceOffset);
            return ValueTask.FromResult(true);
        }

        public ValueTask<int> CaptureAuditLogBatchAsync(
            int batchSize,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(0);
        }

        public ValueTask<PositionLiveStateProjectionProgress> ReadProjectionProgressAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new PositionLiveStateProjectionProgress(
                ProjectionSequence,
                LastEventAppliedAtUtc));
        }

        public ValueTask<IReadOnlyList<PositionLiveStateProjectionItem>> ReadProjectionFactsAsync(
            long afterSequenceId,
            int batchSize,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<PositionLiveStateProjectionItem> result = _projectionFacts
                .Where(item => item.SequenceId > afterSequenceId)
                .OrderBy(item => item.SequenceId)
                .Take(batchSize)
                .ToArray();
            return ValueTask.FromResult(result);
        }

        public ValueTask<bool> ApplyProjectionFactAsync(
            PositionLiveStateProjectionItem item,
            PositionLiveStateProjectionUpdate? update,
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
            if (update is not null)
            {
                ProjectionUpdates.Add(update);
            }

            return ValueTask.FromResult(true);
        }
    }
}
