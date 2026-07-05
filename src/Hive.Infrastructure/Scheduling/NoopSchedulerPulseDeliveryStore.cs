using Hive.Domain.Scheduling;

namespace Hive.Infrastructure.Scheduling;

public sealed class NoopSchedulerPulseDeliveryStore : ISchedulerPulseDeliveryStore
{
    public static NoopSchedulerPulseDeliveryStore Instance { get; } = new();

    private NoopSchedulerPulseDeliveryStore()
    {
    }

    public Task<SchedulerPulseDeliveryState> RecordFiredAsync(
        SchedulerPulseDeliveryRecord delivery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        return Task.FromResult(new SchedulerPulseDeliveryState(
            delivery.IdempotencyKey,
            delivery.MessageId,
            delivery.ThreadId,
            SchedulerPulseDeliveryStatus.Fired,
            attemptCount: 1,
            delivery.OccurredAtUtc,
            reason: null));
    }

    public Task<SchedulerPulseDeliveryState> MarkDeliveredAsync(
        PulseIdempotencyKey idempotencyKey,
        DateTimeOffset occurredAtUtc,
        SchedulerPulseDeliveryReason? reason = null,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Scheduler pulse delivery state is unavailable because PostgreSQL is not configured.");

    public Task<SchedulerPulseDeliveryState> MarkSkippedAsync(
        PulseIdempotencyKey idempotencyKey,
        DateTimeOffset occurredAtUtc,
        SchedulerPulseDeliveryReason reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reason);
        throw new InvalidOperationException("Scheduler pulse delivery state is unavailable because PostgreSQL is not configured.");
    }

    public Task<SchedulerPulseDeliveryState> MarkFailedAsync(
        PulseIdempotencyKey idempotencyKey,
        DateTimeOffset occurredAtUtc,
        SchedulerPulseDeliveryReason reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reason);
        throw new InvalidOperationException("Scheduler pulse delivery state is unavailable because PostgreSQL is not configured.");
    }

    public Task<SchedulerPulseDeliveryState?> FindAsync(
        PulseIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<SchedulerPulseDeliveryState?>(null);

    public Task<IReadOnlyList<SchedulerPulseDeliveryHistoryEntry>> ReadHistoryAsync(
        PulseIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SchedulerPulseDeliveryHistoryEntry>>([]);
}
