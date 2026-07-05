using System.Collections.Immutable;
using Hive.Infrastructure.Organization.Registry;
using Hive.Infrastructure.Scheduling;

namespace Hive.Actors.Scheduling;

internal enum SchedulerScheduleReconciliationOperationKind
{
    Create,
    Update,
    Pause,
    Remove,
    Unchanged,
}

internal sealed record SchedulerScheduleReconciliationOperation
{
    public SchedulerScheduleReconciliationOperation(
        SchedulerScheduleReconciliationOperationKind kind,
        SchedulerScheduleKey key,
        SchedulerScheduleMaterialization? current,
        SchedulerScheduleMaterialization? declared)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Kind = kind;
        Current = current;
        Declared = declared;
    }

    public SchedulerScheduleReconciliationOperationKind Kind { get; }

    public SchedulerScheduleKey Key { get; }

    public SchedulerScheduleMaterialization? Current { get; }

    public SchedulerScheduleMaterialization? Declared { get; }
}

internal sealed record SchedulerScheduleReconciliationDiff
{
    private SchedulerScheduleReconciliationDiff(
        long? previousRegistryVersion,
        string? previousRegistryFingerprint,
        long? newRegistryVersion,
        string? newRegistryFingerprint,
        bool isRegistryChanged,
        ImmutableArray<SchedulerScheduleReconciliationOperation> operations,
        ImmutableArray<SchedulerScheduleMaterialization> activeMaterializations)
    {
        PreviousRegistryVersion = previousRegistryVersion;
        PreviousRegistryFingerprint = previousRegistryFingerprint;
        NewRegistryVersion = newRegistryVersion;
        NewRegistryFingerprint = newRegistryFingerprint;
        IsRegistryChanged = isRegistryChanged;
        Operations = operations;
        ActiveMaterializations = activeMaterializations;
    }

    public static SchedulerScheduleReconciliationDiff Empty { get; } = new(
        previousRegistryVersion: null,
        previousRegistryFingerprint: null,
        newRegistryVersion: null,
        newRegistryFingerprint: null,
        isRegistryChanged: false,
        ImmutableArray<SchedulerScheduleReconciliationOperation>.Empty,
        ImmutableArray<SchedulerScheduleMaterialization>.Empty);

    public long? PreviousRegistryVersion { get; }

    public string? PreviousRegistryFingerprint { get; }

    public long? NewRegistryVersion { get; }

    public string? NewRegistryFingerprint { get; }

    public bool IsRegistryChanged { get; }

    public ImmutableArray<SchedulerScheduleReconciliationOperation> Operations { get; }

    internal ImmutableArray<SchedulerScheduleMaterialization> ActiveMaterializations { get; }

    public static SchedulerScheduleReconciliationDiff Calculate(
        SchedulerCoordinatorState current,
        OrganizationRegistrySnapshot snapshot,
        RegistryScheduleLoadResult loaded)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(loaded);

        if (!HasRegistryChanged(current, snapshot))
        {
            return new SchedulerScheduleReconciliationDiff(
                current.RegistryVersion,
                current.RegistryFingerprint,
                snapshot.Version,
                snapshot.Fingerprint,
                isRegistryChanged: false,
                ImmutableArray<SchedulerScheduleReconciliationOperation>.Empty,
                current.Materializations);
        }

        var currentByKey = current.Materializations.ToDictionary(
            materialization => materialization.Key);
        var declaredKeys = new HashSet<SchedulerScheduleKey>();
        var operations = ImmutableArray.CreateBuilder<SchedulerScheduleReconciliationOperation>();
        var activeMaterializations = ImmutableArray.CreateBuilder<SchedulerScheduleMaterialization>();

        foreach (var declared in loaded.Schedules.OrderBy(
            schedule => SchedulerScheduleKey.From(
                schedule.OrganizationId,
                schedule.PositionId,
                schedule.Definition.Id).Value,
            StringComparer.Ordinal))
        {
            var key = SchedulerScheduleKey.From(
                declared.OrganizationId,
                declared.PositionId,
                declared.Definition.Id);
            declaredKeys.Add(key);

            if (!declared.IsActive)
            {
                if (currentByKey.TryGetValue(key, out var paused))
                {
                    var inactiveDeclaration = ToMaterialization(declared, key);
                    operations.Add(new SchedulerScheduleReconciliationOperation(
                        SchedulerScheduleReconciliationOperationKind.Pause,
                        key,
                        paused,
                        inactiveDeclaration));
                }

                continue;
            }

            var declaredMaterialization = ToMaterialization(declared, key);
            activeMaterializations.Add(declaredMaterialization);

            if (!currentByKey.TryGetValue(key, out var currentMaterialization))
            {
                operations.Add(new SchedulerScheduleReconciliationOperation(
                    SchedulerScheduleReconciliationOperationKind.Create,
                    key,
                    current: null,
                    declaredMaterialization));
                continue;
            }

            operations.Add(new SchedulerScheduleReconciliationOperation(
                AreSameMaterialization(currentMaterialization, declaredMaterialization)
                    ? SchedulerScheduleReconciliationOperationKind.Unchanged
                    : SchedulerScheduleReconciliationOperationKind.Update,
                key,
                currentMaterialization,
                declaredMaterialization));
        }

        foreach (var removed in current.Materializations.Where(
            materialization => !declaredKeys.Contains(materialization.Key)))
        {
            operations.Add(new SchedulerScheduleReconciliationOperation(
                SchedulerScheduleReconciliationOperationKind.Remove,
                removed.Key,
                removed,
                declared: null));
        }

        return new SchedulerScheduleReconciliationDiff(
            current.RegistryVersion,
            current.RegistryFingerprint,
            snapshot.Version,
            snapshot.Fingerprint,
            isRegistryChanged: true,
            operations
                .OrderBy(operation => operation.Key.Value, StringComparer.Ordinal)
                .ToImmutableArray(),
            activeMaterializations.ToImmutable());
    }

    private static bool HasRegistryChanged(
        SchedulerCoordinatorState current,
        OrganizationRegistrySnapshot snapshot) =>
        current.RegistryVersion != snapshot.Version
        || !string.Equals(current.RegistryFingerprint, snapshot.Fingerprint, StringComparison.Ordinal);

    private static SchedulerScheduleMaterialization ToMaterialization(
        LoadedRegistrySchedule schedule,
        SchedulerScheduleKey key) =>
        new(
            key,
            schedule.Definition,
            schedule.WorkingHours,
            schedule.UpdatedAtUtc);

    private static bool AreSameMaterialization(
        SchedulerScheduleMaterialization current,
        SchedulerScheduleMaterialization declared) =>
        current.Definition == declared.Definition
        && current.WorkingHours == declared.WorkingHours
        && current.DeclaredAtUtc == declared.DeclaredAtUtc;
}
