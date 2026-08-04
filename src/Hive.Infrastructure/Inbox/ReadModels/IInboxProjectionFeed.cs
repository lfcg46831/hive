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
}
