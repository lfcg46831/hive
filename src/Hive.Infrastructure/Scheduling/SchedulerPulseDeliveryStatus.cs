namespace Hive.Infrastructure.Scheduling;

public enum SchedulerPulseDeliveryStatus
{
    Registered,
    Fired,
    Delivered,
    Skipped,
    Failed,
    Redelivered,
}
