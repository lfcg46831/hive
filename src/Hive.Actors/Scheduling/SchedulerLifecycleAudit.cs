using Hive.Domain.Identity;
using Hive.Domain.Scheduling;
using Hive.Infrastructure.Scheduling;

namespace Hive.Actors.Scheduling;

internal enum SchedulerLifecycleAuditStage
{
    Materialized,
    Fired,
    Delivered,
    Skipped,
    Failed,
    Redelivered,
    CatchUp,
}

internal enum SchedulerLifecycleAuditOutcome
{
    Accepted,
    Skipped,
    Failed,
}

internal enum SchedulerLifecycleAuditSource
{
    Direct,
    Quartz,
    CatchUp,
}

internal sealed record SchedulerLifecycleAuditRecord
{
    public SchedulerLifecycleAuditRecord(
        DateTimeOffset occurredAtUtc,
        SchedulerLifecycleAuditStage stage,
        SchedulerLifecycleAuditOutcome outcome,
        OrganizationId? organizationId,
        PositionId? positionId,
        ScheduleId? scheduleId,
        TemporalWindow? window,
        PulseIdempotencyKey? idempotencyKey,
        MessageId? messageId,
        ThreadId? threadId,
        SchedulerPulseDeliveryReason? reason,
        SchedulerLifecycleAuditSource? source,
        long? registryVersion,
        string? registryFingerprint,
        SchedulerQuartzIdentity? quartzIdentity)
    {
        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Scheduler lifecycle audit timestamps must be expressed as UTC offsets.",
                nameof(occurredAtUtc));
        }

        if (!Enum.IsDefined(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "Scheduler lifecycle audit stage is not defined.");
        }

        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Scheduler lifecycle audit outcome is not defined.");
        }

        if (source.HasValue && !Enum.IsDefined(source.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(source), source, "Scheduler lifecycle audit source is not defined.");
        }

        OccurredAtUtc = occurredAtUtc;
        Stage = stage;
        Outcome = outcome;
        OrganizationId = organizationId;
        PositionId = positionId;
        ScheduleId = scheduleId;
        Window = window;
        IdempotencyKey = idempotencyKey;
        MessageId = messageId;
        ThreadId = threadId;
        Reason = reason;
        Source = source;
        RegistryVersion = registryVersion;
        RegistryFingerprint = registryFingerprint;
        QuartzIdentity = quartzIdentity;
    }

    public DateTimeOffset OccurredAtUtc { get; }

    public SchedulerLifecycleAuditStage Stage { get; }

    public SchedulerLifecycleAuditOutcome Outcome { get; }

    public OrganizationId? OrganizationId { get; }

    public PositionId? PositionId { get; }

    public ScheduleId? ScheduleId { get; }

    public TemporalWindow? Window { get; }

    public PulseIdempotencyKey? IdempotencyKey { get; }

    public MessageId? MessageId { get; }

    public ThreadId? ThreadId { get; }

    public SchedulerPulseDeliveryReason? Reason { get; }

    public SchedulerLifecycleAuditSource? Source { get; }

    public long? RegistryVersion { get; }

    public string? RegistryFingerprint { get; }

    public SchedulerQuartzIdentity? QuartzIdentity { get; }

    public static SchedulerLifecycleAuditRecord Materialized(
        DateTimeOffset occurredAtUtc,
        SchedulerScheduleMaterialization materialization,
        SchedulerQuartzIdentity quartzIdentity,
        long? registryVersion,
        string? registryFingerprint)
    {
        ArgumentNullException.ThrowIfNull(materialization);
        ArgumentNullException.ThrowIfNull(quartzIdentity);

        return new SchedulerLifecycleAuditRecord(
            occurredAtUtc,
            SchedulerLifecycleAuditStage.Materialized,
            SchedulerLifecycleAuditOutcome.Accepted,
            materialization.Key.Organization,
            materialization.Key.Position,
            materialization.Key.Schedule,
            window: null,
            idempotencyKey: null,
            messageId: null,
            threadId: null,
            reason: null,
            source: null,
            registryVersion,
            registryFingerprint,
            quartzIdentity);
    }

    public static SchedulerLifecycleAuditRecord Dispatch(
        DateTimeOffset occurredAtUtc,
        SchedulerLifecycleAuditStage stage,
        SchedulerLifecycleAuditOutcome outcome,
        SchedulerScheduleDispatch dispatch,
        SchedulerLifecycleAuditSource source,
        SchedulerPulseDeliveryReason? reason = null)
    {
        ArgumentNullException.ThrowIfNull(dispatch);

        return FromDelivery(
            occurredAtUtc,
            stage,
            outcome,
            dispatch.Key,
            dispatch.Window,
            dispatch.IdempotencyKey,
            dispatch.Pulse.Id,
            dispatch.Pulse.Thread,
            source,
            reason);
    }

    public static SchedulerLifecycleAuditRecord Delivery(
        DateTimeOffset occurredAtUtc,
        SchedulerLifecycleAuditStage stage,
        SchedulerLifecycleAuditOutcome outcome,
        SchedulerScheduleKey key,
        SchedulerPulseDeliveryRecord delivery,
        SchedulerLifecycleAuditSource source,
        SchedulerPulseDeliveryReason? reason = null)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        return FromDelivery(
            occurredAtUtc,
            stage,
            outcome,
            key,
            delivery.IdempotencyKey.Window,
            delivery.IdempotencyKey,
            delivery.MessageId,
            delivery.ThreadId,
            source,
            reason);
    }

    private static SchedulerLifecycleAuditRecord FromDelivery(
        DateTimeOffset occurredAtUtc,
        SchedulerLifecycleAuditStage stage,
        SchedulerLifecycleAuditOutcome outcome,
        SchedulerScheduleKey key,
        TemporalWindow window,
        PulseIdempotencyKey idempotencyKey,
        MessageId messageId,
        ThreadId threadId,
        SchedulerLifecycleAuditSource source,
        SchedulerPulseDeliveryReason? reason)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(idempotencyKey);
        ArgumentNullException.ThrowIfNull(messageId);
        ArgumentNullException.ThrowIfNull(threadId);

        return new SchedulerLifecycleAuditRecord(
            occurredAtUtc,
            stage,
            outcome,
            key.Organization,
            key.Position,
            key.Schedule,
            window,
            idempotencyKey,
            messageId,
            threadId,
            reason,
            source,
            registryVersion: null,
            registryFingerprint: null,
            quartzIdentity: null);
    }
}

internal interface ISchedulerLifecycleAuditSink
{
    void Publish(SchedulerLifecycleAuditRecord record);
}

internal sealed class NoopSchedulerLifecycleAuditSink : ISchedulerLifecycleAuditSink
{
    public static NoopSchedulerLifecycleAuditSink Instance { get; } = new();

    private NoopSchedulerLifecycleAuditSink()
    {
    }

    public void Publish(SchedulerLifecycleAuditRecord record)
    {
    }
}
