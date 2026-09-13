using Hive.Domain.Events;

namespace Hive.Application.Events;

public enum DomainEventDetectionCycleOutcome { NotDue, Committed, Conflict }

/// <summary>One periodic evaluation. Scheduling and durable source/store adapters are composed later.</summary>
public sealed class DomainEventDetectionCycle
{
    private readonly IDomainEventDetectionStore _store;
    private readonly TimeProvider _clock;

    public DomainEventDetectionCycle(IDomainEventDetectionStore store, TimeProvider clock, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        _store = store;
        _clock = clock;
        Interval = interval;
    }

    public TimeSpan Interval { get; }

    public async ValueTask<DomainEventDetectionCycleOutcome> EvaluateAsync(IDomainEventDetector detector,
        EventSubscriptionsSnapshot subscriptions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(subscriptions);
        cancellationToken.ThrowIfCancellationRequested();
        var id = new DomainEventDetectorId(subscriptions.OrganizationId, detector.EventType);
        var checkpoint = await _store.ReadCheckpointAsync(id, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (checkpoint is not null && checkpoint.DetectorId != id)
            throw new InvalidOperationException("Store returned a checkpoint for another detector partition.");
        var now = _clock.GetUtcNow().ToUniversalTime();
        if (checkpoint is not null && now - checkpoint.EvaluatedAtUtc < Interval)
            return DomainEventDetectionCycleOutcome.NotDue;

        var context = new DomainEventDetectionContext(id, subscriptions, checkpoint, now);
        var result = await detector.EvaluateAsync(context, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var commit = new DomainEventDetectionCommit(context, result);
        return await _store.TryCommitAsync(commit, cancellationToken)
            ? DomainEventDetectionCycleOutcome.Committed
            : DomainEventDetectionCycleOutcome.Conflict;
    }
}
