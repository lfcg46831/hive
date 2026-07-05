using Hive.Domain.Identity;
using Hive.Domain.Scheduling;

namespace Hive.Infrastructure.Scheduling;

public sealed record SchedulerPulseDeliveryRecord
{
    public SchedulerPulseDeliveryRecord(
        PulseIdempotencyKey idempotencyKey,
        MessageId messageId,
        ThreadId threadId,
        DateTimeOffset occurredAtUtc)
    {
        IdempotencyKey = idempotencyKey ?? throw new ArgumentNullException(nameof(idempotencyKey));
        MessageId = messageId ?? throw new ArgumentNullException(nameof(messageId));
        ThreadId = threadId ?? throw new ArgumentNullException(nameof(threadId));
        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Scheduler delivery timestamps must be expressed as UTC offsets.",
                nameof(occurredAtUtc));
        }

        OccurredAtUtc = occurredAtUtc;
    }

    public PulseIdempotencyKey IdempotencyKey { get; }

    public MessageId MessageId { get; }

    public ThreadId ThreadId { get; }

    public DateTimeOffset OccurredAtUtc { get; }
}
