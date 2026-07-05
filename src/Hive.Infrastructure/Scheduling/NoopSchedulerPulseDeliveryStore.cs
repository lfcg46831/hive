using Hive.Domain.Scheduling;

namespace Hive.Infrastructure.Scheduling;

public sealed class NoopSchedulerPulseDeliveryStore : ISchedulerPulseDeliveryStore
{
    public static NoopSchedulerPulseDeliveryStore Instance { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<PulseIdempotencyKey, SchedulerPulseDeliveryState> _states = [];

    private NoopSchedulerPulseDeliveryStore()
    {
    }

    public Task<SchedulerPulseDeliveryState> RecordFiredAsync(
        SchedulerPulseDeliveryRecord delivery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        lock (_gate)
        {
            var status = _states.ContainsKey(delivery.IdempotencyKey)
                ? SchedulerPulseDeliveryStatus.Redelivered
                : SchedulerPulseDeliveryStatus.Fired;
            var attempts = _states.TryGetValue(delivery.IdempotencyKey, out var existing)
                ? existing.AttemptCount + 1
                : 1;
            var state = new SchedulerPulseDeliveryState(
                delivery.IdempotencyKey,
                delivery.MessageId,
                delivery.ThreadId,
                status,
                attempts,
                delivery.OccurredAtUtc,
                reason: null);
            _states[delivery.IdempotencyKey] = state;
            return Task.FromResult(state);
        }
    }

    public Task<SchedulerPulseDeliveryState> MarkDeliveredAsync(
        PulseIdempotencyKey idempotencyKey,
        DateTimeOffset occurredAtUtc,
        SchedulerPulseDeliveryReason? reason = null,
        CancellationToken cancellationToken = default) =>
        MarkAsync(idempotencyKey, SchedulerPulseDeliveryStatus.Delivered, occurredAtUtc, reason);

    public Task<SchedulerPulseDeliveryState> MarkSkippedAsync(
        PulseIdempotencyKey idempotencyKey,
        DateTimeOffset occurredAtUtc,
        SchedulerPulseDeliveryReason reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return MarkAsync(idempotencyKey, SchedulerPulseDeliveryStatus.Skipped, occurredAtUtc, reason);
    }

    public Task<SchedulerPulseDeliveryState> MarkFailedAsync(
        PulseIdempotencyKey idempotencyKey,
        DateTimeOffset occurredAtUtc,
        SchedulerPulseDeliveryReason reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return MarkAsync(idempotencyKey, SchedulerPulseDeliveryStatus.Failed, occurredAtUtc, reason);
    }

    public Task<SchedulerPulseDeliveryState?> FindAsync(
        PulseIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(idempotencyKey);
        lock (_gate)
        {
            return Task.FromResult(_states.GetValueOrDefault(idempotencyKey));
        }
    }

    public Task<IReadOnlyList<SchedulerPulseDeliveryHistoryEntry>> ReadHistoryAsync(
        PulseIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SchedulerPulseDeliveryHistoryEntry>>([]);

    private Task<SchedulerPulseDeliveryState> MarkAsync(
        PulseIdempotencyKey idempotencyKey,
        SchedulerPulseDeliveryStatus status,
        DateTimeOffset occurredAtUtc,
        SchedulerPulseDeliveryReason? reason)
    {
        ArgumentNullException.ThrowIfNull(idempotencyKey);
        lock (_gate)
        {
            if (!_states.TryGetValue(idempotencyKey, out var existing))
            {
                throw new InvalidOperationException(
                    $"Scheduler pulse delivery '{idempotencyKey.Value}' has not been recorded.");
            }

            var state = new SchedulerPulseDeliveryState(
                idempotencyKey,
                existing.MessageId,
                existing.ThreadId,
                status,
                existing.AttemptCount,
                occurredAtUtc,
                reason);
            _states[idempotencyKey] = state;
            return Task.FromResult(state);
        }
    }
}
