namespace Hive.Infrastructure.Scheduling;

public sealed record SchedulerPulseDeliveryHistoryEntry(
    int Sequence,
    SchedulerPulseDeliveryStatus Status,
    DateTimeOffset OccurredAtUtc,
    SchedulerPulseDeliveryReason? Reason);
