using Hive.Domain.Scheduling;

namespace Hive.Infrastructure.Scheduling;

public interface ISchedulerPulseDeliveryStore
{
    Task<SchedulerPulseDeliveryState> RecordFiredAsync(
        SchedulerPulseDeliveryRecord delivery,
        CancellationToken cancellationToken = default);

    Task<SchedulerPulseDeliveryState> MarkDeliveredAsync(
        PulseIdempotencyKey idempotencyKey,
        DateTimeOffset occurredAtUtc,
        SchedulerPulseDeliveryReason? reason = null,
        CancellationToken cancellationToken = default);

    Task<SchedulerPulseDeliveryState> MarkSkippedAsync(
        PulseIdempotencyKey idempotencyKey,
        DateTimeOffset occurredAtUtc,
        SchedulerPulseDeliveryReason reason,
        CancellationToken cancellationToken = default);

    Task<SchedulerPulseDeliveryState> MarkFailedAsync(
        PulseIdempotencyKey idempotencyKey,
        DateTimeOffset occurredAtUtc,
        SchedulerPulseDeliveryReason reason,
        CancellationToken cancellationToken = default);

    Task<SchedulerPulseDeliveryState?> FindAsync(
        PulseIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SchedulerPulseDeliveryHistoryEntry>> ReadHistoryAsync(
        PulseIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default);
}
