using Hive.Domain.Identity;
using Hive.Domain.Scheduling;

namespace Hive.Infrastructure.Scheduling;

public sealed record SchedulerPulseDeliveryState
{
    public SchedulerPulseDeliveryState(
        PulseIdempotencyKey idempotencyKey,
        MessageId messageId,
        ThreadId threadId,
        SchedulerPulseDeliveryStatus status,
        int attemptCount,
        DateTimeOffset lastOccurredAtUtc,
        SchedulerPulseDeliveryReason? reason)
    {
        IdempotencyKey = idempotencyKey ?? throw new ArgumentNullException(nameof(idempotencyKey));
        MessageId = messageId ?? throw new ArgumentNullException(nameof(messageId));
        ThreadId = threadId ?? throw new ArgumentNullException(nameof(threadId));
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Scheduler pulse delivery status is not defined.");
        }

        if (attemptCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptCount), attemptCount, "Attempt count must be positive.");
        }

        if (lastOccurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Scheduler delivery timestamps must be expressed as UTC offsets.",
                nameof(lastOccurredAtUtc));
        }

        Status = status;
        AttemptCount = attemptCount;
        LastOccurredAtUtc = lastOccurredAtUtc;
        Reason = reason;
    }

    public PulseIdempotencyKey IdempotencyKey { get; }

    public MessageId MessageId { get; }

    public ThreadId ThreadId { get; }

    public SchedulerPulseDeliveryStatus Status { get; }

    public int AttemptCount { get; }

    public DateTimeOffset LastOccurredAtUtc { get; }

    public SchedulerPulseDeliveryReason? Reason { get; }
}
