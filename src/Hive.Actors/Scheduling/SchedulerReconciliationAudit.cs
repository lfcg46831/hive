using System.Collections.Immutable;
using Hive.Infrastructure.Organization.Registry;
using Hive.Infrastructure.Scheduling;

namespace Hive.Actors.Scheduling;

internal enum SchedulerReconciliationAuditOutcome
{
    Accepted,
    Rejected,
}

internal sealed record SchedulerReconciliationAuditReason
{
    public static SchedulerReconciliationAuditReason Initialized { get; } = new(
        "scheduler-configuration-initialized",
        "Scheduler configuration was initialized from a valid registry snapshot.");

    public static SchedulerReconciliationAuditReason Changed { get; } = new(
        "scheduler-configuration-changed",
        "Scheduler configuration changed from the previous accepted registry snapshot.");

    public static SchedulerReconciliationAuditReason Unchanged { get; } = new(
        "scheduler-configuration-unchanged",
        "Scheduler configuration version and fingerprint were unchanged.");

    public static SchedulerReconciliationAuditReason Rejected { get; } = new(
        "scheduler-configuration-rejected",
        "Scheduler configuration snapshot was rejected by the schedule loader.");

    public SchedulerReconciliationAuditReason(string code, string message)
    {
        Code = RequireText(code, nameof(code));
        Message = RequireText(message, nameof(message));
    }

    public string Code { get; }

    public string Message { get; }

    private static string RequireText(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty or whitespace.", parameterName);
        }

        return value.Trim();
    }
}

internal sealed record SchedulerReconciliationAuditRecord
{
    public SchedulerReconciliationAuditRecord(
        DateTimeOffset occurredAtUtc,
        SchedulerReconciliationAuditOutcome outcome,
        long? previousRegistryVersion,
        string? previousRegistryFingerprint,
        long? newRegistryVersion,
        string? newRegistryFingerprint,
        SchedulerReconciliationAuditReason reason,
        ImmutableArray<SchedulerScheduleReconciliationOperation> operations,
        ImmutableArray<RegistryScheduleLoadError> errors)
    {
        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Scheduler reconciliation audit timestamps must be expressed as UTC offsets.",
                nameof(occurredAtUtc));
        }

        OccurredAtUtc = occurredAtUtc;
        Outcome = outcome;
        PreviousRegistryVersion = previousRegistryVersion;
        PreviousRegistryFingerprint = previousRegistryFingerprint;
        NewRegistryVersion = newRegistryVersion;
        NewRegistryFingerprint = newRegistryFingerprint;
        Reason = reason ?? throw new ArgumentNullException(nameof(reason));
        Operations = operations.IsDefault
            ? ImmutableArray<SchedulerScheduleReconciliationOperation>.Empty
            : operations;
        Errors = errors.IsDefault
            ? ImmutableArray<RegistryScheduleLoadError>.Empty
            : errors;
    }

    public DateTimeOffset OccurredAtUtc { get; }

    public SchedulerReconciliationAuditOutcome Outcome { get; }

    public long? PreviousRegistryVersion { get; }

    public string? PreviousRegistryFingerprint { get; }

    public long? NewRegistryVersion { get; }

    public string? NewRegistryFingerprint { get; }

    public SchedulerReconciliationAuditReason Reason { get; }

    public ImmutableArray<SchedulerScheduleReconciliationOperation> Operations { get; }

    public ImmutableArray<RegistryScheduleLoadError> Errors { get; }

    public static SchedulerReconciliationAuditRecord Accepted(
        DateTimeOffset occurredAtUtc,
        SchedulerScheduleReconciliationDiff diff) =>
        new(
            occurredAtUtc,
            SchedulerReconciliationAuditOutcome.Accepted,
            diff.PreviousRegistryVersion,
            diff.PreviousRegistryFingerprint,
            diff.NewRegistryVersion,
            diff.NewRegistryFingerprint,
            AcceptedReason(diff),
            diff.Operations,
            ImmutableArray<RegistryScheduleLoadError>.Empty);

    public static SchedulerReconciliationAuditRecord Rejected(
        DateTimeOffset occurredAtUtc,
        SchedulerCoordinatorState current,
        OrganizationRegistrySnapshot snapshot,
        ImmutableArray<RegistryScheduleLoadError> errors)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(snapshot);

        return new SchedulerReconciliationAuditRecord(
            occurredAtUtc,
            SchedulerReconciliationAuditOutcome.Rejected,
            current.RegistryVersion,
            current.RegistryFingerprint,
            snapshot.Version,
            snapshot.Fingerprint,
            SchedulerReconciliationAuditReason.Rejected,
            ImmutableArray<SchedulerScheduleReconciliationOperation>.Empty,
            errors);
    }

    private static SchedulerReconciliationAuditReason AcceptedReason(
        SchedulerScheduleReconciliationDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);

        if (!diff.IsRegistryChanged)
        {
            return SchedulerReconciliationAuditReason.Unchanged;
        }

        return diff.PreviousRegistryVersion is null
               && diff.PreviousRegistryFingerprint is null
            ? SchedulerReconciliationAuditReason.Initialized
            : SchedulerReconciliationAuditReason.Changed;
    }
}

internal interface ISchedulerReconciliationAuditSink
{
    void Publish(SchedulerReconciliationAuditRecord record);
}

internal sealed class NoopSchedulerReconciliationAuditSink : ISchedulerReconciliationAuditSink
{
    public static NoopSchedulerReconciliationAuditSink Instance { get; } = new();

    private NoopSchedulerReconciliationAuditSink()
    {
    }

    public void Publish(SchedulerReconciliationAuditRecord record)
    {
    }
}
