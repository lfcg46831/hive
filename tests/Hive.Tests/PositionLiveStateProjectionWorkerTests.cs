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
        var events = new[]
        {
            JournalEvent(3, MessageReceived()),
            JournalEvent(5, new PositionPassivated(OccurredAt.AddMinutes(1), "idle")),
            JournalEvent(8, new PositionPassivated(OccurredAt.AddMinutes(2), "idle")),
        };
        var feed = new RecordingFeed();
        var firstJournal = new RecordingJournal(events[..2]);
        var firstWorker = new PositionLiveStateProjectionWorker(firstJournal, feed);

        var firstCaptured = await firstWorker.CapturePositionJournalBatchAsync(CancellationToken.None);
        var restartedJournal = new RecordingJournal(events);
        var restartedWorker = new PositionLiveStateProjectionWorker(restartedJournal, feed);
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

    private static PositionLiveStateProjectionJournalEvent JournalEvent(
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
        IReadOnlyCollection<PositionLiveStateProjectionJournalEvent> events)
        : IPositionLiveStateProjectionJournal
    {
        public List<long> RequestedAfterOffsets { get; } = [];

        public Task<IReadOnlyList<PositionLiveStateProjectionJournalEvent>> ReadBatchAsync(
            long afterOffset,
            int batchSize,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedAfterOffsets.Add(afterOffset);
            IReadOnlyList<PositionLiveStateProjectionJournalEvent> result = events
                .Where(item => item.Offset > afterOffset)
                .OrderBy(item => item.Offset)
                .Take(batchSize)
                .ToArray();
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingFeed(long? failAtOffset = null) : IPositionLiveStateProjectionFeed
    {
        public bool IsConfigured => true;

        public long PositionCheckpoint { get; private set; }

        public List<long> CapturedOffsets { get; } = [];

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
            return ValueTask.FromResult(true);
        }

        public ValueTask<int> CaptureAuditLogBatchAsync(
            int batchSize,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(0);
        }
    }
}
