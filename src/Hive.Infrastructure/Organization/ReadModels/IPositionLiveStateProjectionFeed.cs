namespace Hive.Infrastructure.Organization.ReadModels;

public interface IPositionLiveStateProjectionFeed
{
    bool IsConfigured { get; }

    ValueTask<long> ReadCheckpointAsync(
        PositionLiveStateProjectionSubscription subscription,
        CancellationToken cancellationToken = default);

    ValueTask<bool> CapturePositionJournalAsync(
        long sourceOffset,
        IReadOnlyCollection<PositionLiveStateProjectionFact> facts,
        CancellationToken cancellationToken = default);

    ValueTask<bool> AdvancePositionJournalCheckpointAsync(
        long sourceOffset,
        CancellationToken cancellationToken = default);

    ValueTask<int> CaptureAuditLogBatchAsync(
        int batchSize,
        CancellationToken cancellationToken = default);

    ValueTask<PositionLiveStateProjectionProgress> ReadProjectionProgressAsync(
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<PositionLiveStateProjectionItem>> ReadProjectionFactsAsync(
        long afterSequenceId,
        int batchSize,
        CancellationToken cancellationToken = default);

    ValueTask<bool> ApplyProjectionFactAsync(
        PositionLiveStateProjectionItem item,
        PositionLiveStateProjectionUpdate? update,
        CancellationToken cancellationToken = default);
}
