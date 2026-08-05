namespace Hive.Infrastructure.Inbox.ReadModels;

public interface IInboxProjectionFeed
{
    bool IsConfigured { get; }

    ValueTask<long> ReadCheckpointAsync(
        InboxProjectionSubscription subscription,
        CancellationToken cancellationToken = default);

    ValueTask<bool> CapturePositionJournalAsync(
        long sourceOffset,
        IReadOnlyCollection<InboxProjectionFact> facts,
        CancellationToken cancellationToken = default);

    ValueTask<int> CaptureAuditLogBatchAsync(
        int batchSize,
        CancellationToken cancellationToken = default);

    ValueTask<InboxProjectionProgress> ReadProjectionProgressAsync(
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<InboxProjectionFactItem>> ReadProjectionFactsAsync(
        long afterSequenceId,
        int batchSize,
        CancellationToken cancellationToken = default);

    ValueTask<bool> ApplyProjectionFactAsync(
        InboxProjectionFactItem item,
        IReadOnlyCollection<InboxProjectionChange> changes,
        CancellationToken cancellationToken = default);

    ValueTask<int> ApplyProjectionChangesAsync(
        long expectedProjectionSequence,
        IReadOnlyCollection<InboxProjectionChange> changes,
        CancellationToken cancellationToken = default);
}
