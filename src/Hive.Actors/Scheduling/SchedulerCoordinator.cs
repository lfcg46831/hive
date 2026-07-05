using System.Collections.Immutable;
using System.Text;
using Akka.Actor;
using Akka.Dispatch;
using Akka.Quartz.Actor;
using Akka.Quartz.Actor.Commands;
using Akka.Quartz.Actor.Events;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Scheduling;
using Hive.Infrastructure.Organization.Registry;
using Hive.Infrastructure.Scheduling;
using Quartz;
using DomainCronExpression = Hive.Domain.Scheduling.CronExpression;

namespace Hive.Actors.Scheduling;

internal static class SchedulerCoordinatorIdentity
{
    public const string LogicalName = "scheduler-coordinator";
    public const string ActorName = LogicalName;
    public const string QuartzActorName = "scheduler-coordinator-quartz";

    // Cluster Singleton wiring (US-F0-09-T03c). The coordinator is materialized exactly once in the
    // cluster as an Akka Cluster Singleton on the agents role: the ClusterSingletonManager actor is
    // hosted under a stable top-level name on every agents node, the active singleton child lives
    // under SingletonName inside it, and a ClusterSingletonProxy under a stable name lets any node
    // reach the single active instance without knowing which node hosts it.
    public const string SingletonManagerName = "scheduler-coordinator";
    public const string SingletonName = "coordinator";
    public const string ProxyName = "scheduler-coordinator-proxy";

    /// <summary>Absolute actor path of the singleton manager the proxy connects to.</summary>
    public const string SingletonManagerPath = "/user/" + SingletonManagerName;
}

internal sealed class SchedulerCoordinator : ReceiveActor
{
    private const string PulseDeliveryFailedCode = "scheduler-pulse-delivery-failed";

    private readonly ISchedulerQuartzAdapter _quartzAdapter;
    private readonly TimeProvider _clock;
    private readonly ISchedulerPulseDeliveryStore _deliveryStore;
    private readonly ISchedulerPulseDispatcher _pulseDispatcher;
    private readonly ISchedulerProactiveBudgetPolicy _proactiveBudgetPolicy;
    private readonly ISchedulerReconciliationAuditSink _reconciliationAudit;
    private readonly ISchedulerLifecycleAuditSink _lifecycleAudit;
    private SchedulerCoordinatorState _state = SchedulerCoordinatorState.Empty;

    public SchedulerCoordinator()
        : this(
            new AkkaQuartzSchedulerAdapter(),
            TimeProvider.System,
            NoopSchedulerPulseDeliveryStore.Instance,
            NoopSchedulerPulseDispatcher.Instance,
            AllowingSchedulerProactiveBudgetPolicy.Instance)
    {
    }

    public SchedulerCoordinator(ISchedulerQuartzAdapter quartzAdapter, TimeProvider clock)
        : this(
            quartzAdapter,
            clock,
            NoopSchedulerPulseDeliveryStore.Instance,
            NoopSchedulerPulseDispatcher.Instance,
            AllowingSchedulerProactiveBudgetPolicy.Instance)
    {
    }

    public SchedulerCoordinator(
        ISchedulerQuartzAdapter quartzAdapter,
        TimeProvider clock,
        ISchedulerPulseDeliveryStore deliveryStore)
        : this(quartzAdapter, clock, deliveryStore, NoopSchedulerPulseDispatcher.Instance)
    {
    }

    public SchedulerCoordinator(
        ISchedulerQuartzAdapter quartzAdapter,
        TimeProvider clock,
        ISchedulerPulseDeliveryStore deliveryStore,
        ISchedulerPulseDispatcher pulseDispatcher)
        : this(
            quartzAdapter,
            clock,
            deliveryStore,
            pulseDispatcher,
            AllowingSchedulerProactiveBudgetPolicy.Instance)
    {
    }

    public SchedulerCoordinator(
        ISchedulerQuartzAdapter quartzAdapter,
        TimeProvider clock,
        ISchedulerPulseDeliveryStore deliveryStore,
        ISchedulerPulseDispatcher pulseDispatcher,
        ISchedulerProactiveBudgetPolicy proactiveBudgetPolicy)
        : this(
            quartzAdapter,
            clock,
            deliveryStore,
            pulseDispatcher,
            proactiveBudgetPolicy,
            NoopSchedulerReconciliationAuditSink.Instance)
    {
    }

    public SchedulerCoordinator(
        ISchedulerQuartzAdapter quartzAdapter,
        TimeProvider clock,
        ISchedulerPulseDeliveryStore deliveryStore,
        ISchedulerPulseDispatcher pulseDispatcher,
        ISchedulerProactiveBudgetPolicy proactiveBudgetPolicy,
        ISchedulerReconciliationAuditSink reconciliationAudit)
        : this(
            quartzAdapter,
            clock,
            deliveryStore,
            pulseDispatcher,
            proactiveBudgetPolicy,
            reconciliationAudit,
            NoopSchedulerLifecycleAuditSink.Instance)
    {
    }

    public SchedulerCoordinator(
        ISchedulerQuartzAdapter quartzAdapter,
        TimeProvider clock,
        ISchedulerPulseDeliveryStore deliveryStore,
        ISchedulerPulseDispatcher pulseDispatcher,
        ISchedulerProactiveBudgetPolicy proactiveBudgetPolicy,
        ISchedulerReconciliationAuditSink reconciliationAudit,
        ISchedulerLifecycleAuditSink lifecycleAudit)
    {
        _quartzAdapter = quartzAdapter ?? throw new ArgumentNullException(nameof(quartzAdapter));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _deliveryStore = deliveryStore ?? throw new ArgumentNullException(nameof(deliveryStore));
        _pulseDispatcher = pulseDispatcher ?? throw new ArgumentNullException(nameof(pulseDispatcher));
        _proactiveBudgetPolicy = proactiveBudgetPolicy ?? throw new ArgumentNullException(nameof(proactiveBudgetPolicy));
        _reconciliationAudit = reconciliationAudit ?? throw new ArgumentNullException(nameof(reconciliationAudit));
        _lifecycleAudit = lifecycleAudit ?? throw new ArgumentNullException(nameof(lifecycleAudit));

        ReceiveAsync<ReconcileSchedulerSchedules>(HandleAsync);
        ReceiveAsync<DispatchSchedulerSchedule>(HandleAsync);
        ReceiveAsync<SchedulerQuartzScheduleFired>(HandleAsync);
        Receive<GetSchedulerCoordinatorState>(_ => Sender.Tell(_state));

        // Diagnostic identity probe (US-F0-09-T03c): reveals the active singleton instance by
        // replying with its own IActorRef. Sent through the ClusterSingletonProxy it lets callers
        // (and tests) locate which node currently hosts the single active coordinator, proving that
        // startup and post-handover restart both keep exactly one active instance.
        Receive<WhereIsSchedulerCoordinator>(_ => Sender.Tell(Self));
    }

    public static Props Props() => Akka.Actor.Props.Create(() => new SchedulerCoordinator());

    public static Props Props(ISchedulerQuartzAdapter quartzAdapter, TimeProvider clock) =>
        Akka.Actor.Props.Create(() => new SchedulerCoordinator(quartzAdapter, clock));

    public static Props Props(
        ISchedulerQuartzAdapter quartzAdapter,
        TimeProvider clock,
        ISchedulerPulseDeliveryStore deliveryStore) =>
        Akka.Actor.Props.Create(() => new SchedulerCoordinator(quartzAdapter, clock, deliveryStore));

    public static Props Props(
        ISchedulerQuartzAdapter quartzAdapter,
        TimeProvider clock,
        ISchedulerPulseDeliveryStore deliveryStore,
        ISchedulerPulseDispatcher pulseDispatcher) =>
        Akka.Actor.Props.Create(() => new SchedulerCoordinator(
            quartzAdapter,
            clock,
            deliveryStore,
            pulseDispatcher));

    public static Props Props(
        ISchedulerQuartzAdapter quartzAdapter,
        TimeProvider clock,
        ISchedulerPulseDeliveryStore deliveryStore,
        ISchedulerPulseDispatcher pulseDispatcher,
        ISchedulerProactiveBudgetPolicy proactiveBudgetPolicy) =>
        Akka.Actor.Props.Create(() => new SchedulerCoordinator(
            quartzAdapter,
            clock,
            deliveryStore,
            pulseDispatcher,
            proactiveBudgetPolicy));

    public static Props Props(
        ISchedulerQuartzAdapter quartzAdapter,
        TimeProvider clock,
        ISchedulerPulseDeliveryStore deliveryStore,
        ISchedulerPulseDispatcher pulseDispatcher,
        ISchedulerProactiveBudgetPolicy proactiveBudgetPolicy,
        ISchedulerReconciliationAuditSink reconciliationAudit) =>
        Akka.Actor.Props.Create(() => new SchedulerCoordinator(
            quartzAdapter,
            clock,
            deliveryStore,
            pulseDispatcher,
            proactiveBudgetPolicy,
            reconciliationAudit));

    public static Props Props(
        ISchedulerQuartzAdapter quartzAdapter,
        TimeProvider clock,
        ISchedulerPulseDeliveryStore deliveryStore,
        ISchedulerPulseDispatcher pulseDispatcher,
        ISchedulerProactiveBudgetPolicy proactiveBudgetPolicy,
        ISchedulerReconciliationAuditSink reconciliationAudit,
        ISchedulerLifecycleAuditSink lifecycleAudit) =>
        Akka.Actor.Props.Create(() => new SchedulerCoordinator(
            quartzAdapter,
            clock,
            deliveryStore,
            pulseDispatcher,
            proactiveBudgetPolicy,
            reconciliationAudit,
            lifecycleAudit));

    private async Task HandleAsync(ReconcileSchedulerSchedules command)
    {
        var replyTo = Sender;
        var occurredAtUtc = _clock.GetUtcNow();
        var loaded = RegistryScheduleLoader.Load(command.Snapshot);
        if (!loaded.IsValid)
        {
            _reconciliationAudit.Publish(SchedulerReconciliationAuditRecord.Rejected(
                occurredAtUtc,
                _state,
                command.Snapshot,
                loaded.Errors));
            replyTo.Tell(SchedulerReconciliationResult.Rejected(
                loaded.Errors,
                _state.LastReconciliationDiff));
            return;
        }

        var diff = SchedulerScheduleReconciliationDiff.Calculate(_state, command.Snapshot, loaded);
        if (!diff.IsRegistryChanged)
        {
            _reconciliationAudit.Publish(SchedulerReconciliationAuditRecord.Accepted(
                occurredAtUtc,
                diff));
            replyTo.Tell(SchedulerReconciliationResult.Accepted(
                _state.Materializations,
                diff));
            return;
        }

        var materializations = diff.ActiveMaterializations;

        _state = _state.WithMaterializations(
            command.Snapshot.Version,
            command.Snapshot.Fingerprint,
            materializations,
            diff);

        var nowUtc = occurredAtUtc;
        foreach (var operation in diff.Operations)
        {
            var scheduledJob = ApplyQuartzOperation(operation);
            if (scheduledJob is not null && operation.Declared is not null)
            {
                _lifecycleAudit.Publish(SchedulerLifecycleAuditRecord.Materialized(
                    nowUtc,
                    operation.Declared,
                    scheduledJob.Identity,
                    diff.NewRegistryVersion,
                    diff.NewRegistryFingerprint));
            }
        }

        foreach (var materialization in materializations)
        {
            await EvaluateMissedWindowAsync(materialization, nowUtc).ConfigureAwait(false);
        }

        _reconciliationAudit.Publish(SchedulerReconciliationAuditRecord.Accepted(
            nowUtc,
            diff));
        replyTo.Tell(SchedulerReconciliationResult.Accepted(materializations, diff));
    }

    private SchedulerQuartzJob? ApplyQuartzOperation(SchedulerScheduleReconciliationOperation operation)
    {
        switch (operation.Kind)
        {
            case SchedulerScheduleReconciliationOperationKind.Create:
            case SchedulerScheduleReconciliationOperationKind.Update:
                var materialization = operation.Declared
                    ?? throw new InvalidOperationException(
                        $"Scheduler reconciliation operation '{operation.Kind}' must include a declared materialization.");
                var job = new SchedulerQuartzJob(
                    materialization.Key,
                    materialization.Definition.Cron,
                    materialization.Definition.TimeZone);
                _quartzAdapter.Schedule(
                    Context,
                    Self,
                    job);
                return job;
            case SchedulerScheduleReconciliationOperationKind.Pause:
            case SchedulerScheduleReconciliationOperationKind.Remove:
                _quartzAdapter.Unschedule(Context, operation.Key);
                return null;
            case SchedulerScheduleReconciliationOperationKind.Unchanged:
                return null;
            default:
                throw new InvalidOperationException(
                    $"Unsupported scheduler reconciliation operation '{operation.Kind}'.");
        }
    }

    private Task HandleAsync(DispatchSchedulerSchedule command)
    {
        return DispatchAsync(
            command.Key,
            command.FiredAtUtc,
            Sender,
            SchedulerLifecycleAuditSource.Direct);
    }

    private Task HandleAsync(SchedulerQuartzScheduleFired command)
    {
        return DispatchAsync(
            command.Key,
            _clock.GetUtcNow(),
            Sender,
            SchedulerLifecycleAuditSource.Quartz);
    }

    private async Task DispatchAsync(
        SchedulerScheduleKey key,
        DateTimeOffset firedAtUtc,
        IActorRef replyTo,
        SchedulerLifecycleAuditSource source)
    {
        var materialization = _state.Materializations.FirstOrDefault(materialization => materialization.Key == key);
        if (materialization is null)
        {
            replyTo.Tell(SchedulerDispatchResult.Rejected(new SchedulerDispatchError(
                "schedule-not-materialized",
                $"Schedule '{key.Value}' is not materialized by the coordinator.")));
            return;
        }

        if (!SchedulerScheduleWindowCalculator.TryCalculate(
                materialization,
                firedAtUtc,
                out var dispatchWindow,
                out var error))
        {
            replyTo.Tell(SchedulerDispatchResult.Rejected(error!));
            return;
        }

        var calculatedWindow = dispatchWindow!;
        var pulse = SchedulerPulseFactory.Build(
            materialization,
            firedAtUtc,
            calculatedWindow.IdempotencyKey);
        var dispatch = new SchedulerScheduleDispatch(
            key,
            firedAtUtc,
            calculatedWindow.Window,
            calculatedWindow.IdempotencyKey,
            pulse);
        var firedState = await _deliveryStore.RecordFiredAsync(
                new SchedulerPulseDeliveryRecord(
                    dispatch.IdempotencyKey,
                    dispatch.Pulse.Id,
                    dispatch.Pulse.Thread,
                    dispatch.FiredAtUtc))
            .ConfigureAwait(false);
        _lifecycleAudit.Publish(SchedulerLifecycleAuditRecord.Dispatch(
            dispatch.FiredAtUtc,
            firedState.Status == SchedulerPulseDeliveryStatus.Redelivered
                ? SchedulerLifecycleAuditStage.Redelivered
                : SchedulerLifecycleAuditStage.Fired,
            SchedulerLifecycleAuditOutcome.Accepted,
            dispatch,
            source));

        var workingHoursDecision = SchedulerDispatchPolicy.Evaluate(
            materialization,
            dispatch,
            hasAvailableProactiveBudget: true);
        if (!workingHoursDecision.IsAllowed)
        {
            await SkipDispatchAsync(dispatch, workingHoursDecision.Reason!, replyTo, source).ConfigureAwait(false);
            return;
        }

        var hasAvailableProactiveBudget = await _proactiveBudgetPolicy
            .HasAvailableBudgetAsync(SchedulerProactiveBudgetRequest.From(materialization, dispatch))
            .ConfigureAwait(false);
        var budgetDecision = SchedulerDispatchPolicy.Evaluate(
            materialization,
            dispatch,
            hasAvailableProactiveBudget);
        if (!budgetDecision.IsAllowed)
        {
            await SkipDispatchAsync(dispatch, budgetDecision.Reason!, replyTo, source).ConfigureAwait(false);
            return;
        }

        try
        {
            await _pulseDispatcher.DeliverAsync(Context, dispatch.Pulse).ConfigureAwait(false);
        }
        catch (Exception)
        {
            var reason = new SchedulerPulseDeliveryReason(
                PulseDeliveryFailedCode,
                "Scheduler pulse delivery to the position shard region failed.");
            await _deliveryStore.MarkFailedAsync(
                    dispatch.IdempotencyKey,
                    dispatch.FiredAtUtc,
                    reason)
                .ConfigureAwait(false);
            _lifecycleAudit.Publish(SchedulerLifecycleAuditRecord.Dispatch(
                dispatch.FiredAtUtc,
                SchedulerLifecycleAuditStage.Failed,
                SchedulerLifecycleAuditOutcome.Failed,
                dispatch,
                source,
                reason));
            replyTo.Tell(SchedulerDispatchResult.Rejected(new SchedulerDispatchError(
                reason.Code,
                reason.Message)));
            return;
        }

        await _deliveryStore.MarkDeliveredAsync(
                dispatch.IdempotencyKey,
                dispatch.FiredAtUtc)
            .ConfigureAwait(false);
        _lifecycleAudit.Publish(SchedulerLifecycleAuditRecord.Dispatch(
            dispatch.FiredAtUtc,
            SchedulerLifecycleAuditStage.Delivered,
            SchedulerLifecycleAuditOutcome.Accepted,
            dispatch,
            source));
        _state = _state.WithPendingDispatch(dispatch);
        replyTo.Tell(SchedulerDispatchResult.Accepted(dispatch));
    }

    private async Task SkipDispatchAsync(
        SchedulerScheduleDispatch dispatch,
        SchedulerPulseDeliveryReason reason,
        IActorRef replyTo,
        SchedulerLifecycleAuditSource source)
    {
        await _deliveryStore.MarkSkippedAsync(
                dispatch.IdempotencyKey,
                dispatch.FiredAtUtc,
                reason)
            .ConfigureAwait(false);
        _lifecycleAudit.Publish(SchedulerLifecycleAuditRecord.Dispatch(
            dispatch.FiredAtUtc,
            SchedulerLifecycleAuditStage.Skipped,
            SchedulerLifecycleAuditOutcome.Skipped,
            dispatch,
            source,
            reason));
        replyTo.Tell(SchedulerDispatchResult.Rejected(new SchedulerDispatchError(
            reason.Code,
            reason.Message)));
    }

    private async Task EvaluateMissedWindowAsync(
        SchedulerScheduleMaterialization materialization,
        DateTimeOffset nowUtc)
    {
        if (!SchedulerMissedWindowEvaluator.TryResolveCandidate(
                materialization,
                nowUtc,
                out var candidate,
                out _))
        {
            return;
        }

        var missedWindow = candidate!;
        var existing = await _deliveryStore.FindAsync(missedWindow.DispatchWindow.IdempotencyKey)
            .ConfigureAwait(false);
        var decision = SchedulerMissedWindowEvaluator.Decide(
            materialization,
            missedWindow,
            existing);

        if (decision.Action == SchedulerMissedWindowAction.None)
        {
            return;
        }

        if (decision.Action == SchedulerMissedWindowAction.CatchUp)
        {
            var catchUpDelivery = SchedulerPulseFactory.BuildDeliveryRecord(
                materialization,
                nowUtc,
                decision.DispatchWindow.IdempotencyKey);
            _lifecycleAudit.Publish(SchedulerLifecycleAuditRecord.Delivery(
                nowUtc,
                SchedulerLifecycleAuditStage.CatchUp,
                SchedulerLifecycleAuditOutcome.Accepted,
                materialization.Key,
                catchUpDelivery,
                SchedulerLifecycleAuditSource.CatchUp));
            await DispatchAsync(
                    materialization.Key,
                    nowUtc,
                    ActorRefs.Nobody,
                    SchedulerLifecycleAuditSource.CatchUp)
                .ConfigureAwait(false);
            return;
        }

        var delivery = SchedulerPulseFactory.BuildDeliveryRecord(
            materialization,
            nowUtc,
            decision.DispatchWindow.IdempotencyKey);

        await _deliveryStore.RecordSkippedAsync(delivery, decision.Reason!).ConfigureAwait(false);
        _lifecycleAudit.Publish(SchedulerLifecycleAuditRecord.Delivery(
            nowUtc,
            SchedulerLifecycleAuditStage.Skipped,
            SchedulerLifecycleAuditOutcome.Skipped,
            materialization.Key,
            delivery,
            SchedulerLifecycleAuditSource.CatchUp,
            decision.Reason));
    }
}

internal sealed record ReconcileSchedulerSchedules
{
    public ReconcileSchedulerSchedules(OrganizationRegistrySnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public OrganizationRegistrySnapshot Snapshot { get; }
}

internal sealed record DispatchSchedulerSchedule
{
    public DispatchSchedulerSchedule(SchedulerScheduleKey key, DateTimeOffset firedAtUtc)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        if (firedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Scheduler dispatch timestamps must be expressed as UTC offsets.",
                nameof(firedAtUtc));
        }

        FiredAtUtc = firedAtUtc;
    }

    public SchedulerScheduleKey Key { get; }

    public DateTimeOffset FiredAtUtc { get; }
}

internal sealed record SchedulerQuartzScheduleFired
{
    public SchedulerQuartzScheduleFired(SchedulerScheduleKey key)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
    }

    public SchedulerScheduleKey Key { get; }
}

internal sealed record GetSchedulerCoordinatorState
{
    public static GetSchedulerCoordinatorState Instance { get; } = new();

    private GetSchedulerCoordinatorState()
    {
    }
}

internal sealed record WhereIsSchedulerCoordinator
{
    // Parameterless (public) so it round-trips through Akka's default serializer when a proxy on
    // one node forwards the probe to the active singleton hosted on another node.
    public static WhereIsSchedulerCoordinator Instance { get; } = new();
}

internal sealed record SchedulerScheduleKey
{
    private SchedulerScheduleKey(
        OrganizationId organization,
        PositionId position,
        ScheduleId schedule)
    {
        Organization = organization;
        Position = position;
        Schedule = schedule;
        Value = $"{PositionEntityId.From(organization, position).Value}/{schedule.Value}";
    }

    public OrganizationId Organization { get; }

    public PositionId Position { get; }

    public ScheduleId Schedule { get; }

    public string Value { get; }

    public static SchedulerScheduleKey From(
        OrganizationId organization,
        PositionId position,
        ScheduleId schedule)
    {
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(schedule);

        return new SchedulerScheduleKey(organization, position, schedule);
    }

    public override string ToString() => Value;
}

internal sealed record SchedulerQuartzIdentity
{
    public const string JobGroupName = "hive-scheduler-jobs";
    public const string TriggerGroupName = "hive-scheduler-triggers";

    private SchedulerQuartzIdentity(
        string jobGroup,
        string jobName,
        string triggerGroup,
        string triggerName)
    {
        JobGroup = jobGroup;
        JobName = jobName;
        TriggerGroup = triggerGroup;
        TriggerName = triggerName;
    }

    public string JobGroup { get; }

    public string JobName { get; }

    public string TriggerGroup { get; }

    public string TriggerName { get; }

    public static SchedulerQuartzIdentity From(SchedulerScheduleKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var organization = SanitizeSegment(key.Organization.Value);
        var position = SanitizeSegment(key.Position.Value);
        var schedule = SanitizeSegment(key.Schedule.Value);
        var suffix = $"{organization}--{position}--{schedule}";

        return new SchedulerQuartzIdentity(
            JobGroupName,
            $"job--{suffix}",
            TriggerGroupName,
            $"trigger--{suffix}");
    }

    private static string SanitizeSegment(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (IsQuartzTokenCharacter(character))
            {
                builder.Append(character);
                continue;
            }

            builder.Append("_u");
            builder.Append(((int)character).ToString("x4"));
            builder.Append('_');
        }

        return builder.ToString();
    }

    private static bool IsQuartzTokenCharacter(char character) =>
        character is >= 'a' and <= 'z'
        || character is >= 'A' and <= 'Z'
        || character is >= '0' and <= '9'
        || character is '-' or '.';
}

internal sealed record SchedulerScheduleMaterialization
{
    public SchedulerScheduleMaterialization(
        SchedulerScheduleKey key,
        ScheduleDefinition definition,
        LoadedScheduleWorkingHours workingHours,
        DateTimeOffset? declaredAtUtc = null)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        WorkingHours = workingHours ?? throw new ArgumentNullException(nameof(workingHours));
        DeclaredAtUtc = declaredAtUtc ?? DateTimeOffset.MinValue;
        if (DeclaredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Scheduler materialization declaration timestamps must be expressed as UTC offsets.",
                nameof(declaredAtUtc));
        }
    }

    public SchedulerScheduleKey Key { get; }

    public ScheduleDefinition Definition { get; }

    public LoadedScheduleWorkingHours WorkingHours { get; }

    public DateTimeOffset DeclaredAtUtc { get; }
}

internal sealed record SchedulerScheduleDispatch
{
    public SchedulerScheduleDispatch(
        SchedulerScheduleKey key,
        DateTimeOffset firedAtUtc,
        TemporalWindow window,
        PulseIdempotencyKey idempotencyKey,
        Pulse pulse)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        if (firedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Scheduler dispatch timestamps must be expressed as UTC offsets.",
                nameof(firedAtUtc));
        }

        FiredAtUtc = firedAtUtc;
        Window = window ?? throw new ArgumentNullException(nameof(window));
        IdempotencyKey = idempotencyKey ?? throw new ArgumentNullException(nameof(idempotencyKey));
        Pulse = pulse ?? throw new ArgumentNullException(nameof(pulse));
    }

    public SchedulerScheduleKey Key { get; }

    public DateTimeOffset FiredAtUtc { get; }

    public TemporalWindow Window { get; }

    public PulseIdempotencyKey IdempotencyKey { get; }

    public Pulse Pulse { get; }
}

internal sealed record SchedulerCoordinatorState
{
    private SchedulerCoordinatorState(
        long? registryVersion,
        string? registryFingerprint,
        ImmutableArray<SchedulerScheduleMaterialization> materializations,
        SchedulerScheduleReconciliationDiff lastReconciliationDiff,
        ImmutableArray<SchedulerScheduleDispatch> pendingDispatches)
    {
        RegistryVersion = registryVersion;
        RegistryFingerprint = registryFingerprint;
        Materializations = materializations;
        LastReconciliationDiff = lastReconciliationDiff;
        PendingDispatches = pendingDispatches;
    }

    public static SchedulerCoordinatorState Empty { get; } = new(
        registryVersion: null,
        registryFingerprint: null,
        ImmutableArray<SchedulerScheduleMaterialization>.Empty,
        SchedulerScheduleReconciliationDiff.Empty,
        ImmutableArray<SchedulerScheduleDispatch>.Empty);

    public long? RegistryVersion { get; }

    public string? RegistryFingerprint { get; }

    public ImmutableArray<SchedulerScheduleMaterialization> Materializations { get; }

    public SchedulerScheduleReconciliationDiff LastReconciliationDiff { get; }

    public ImmutableArray<SchedulerScheduleDispatch> PendingDispatches { get; }

    public SchedulerCoordinatorState WithMaterializations(
        long registryVersion,
        string registryFingerprint,
        ImmutableArray<SchedulerScheduleMaterialization> materializations,
        SchedulerScheduleReconciliationDiff diff) =>
        new(
            registryVersion,
            registryFingerprint ?? throw new ArgumentNullException(nameof(registryFingerprint)),
            materializations,
            diff ?? throw new ArgumentNullException(nameof(diff)),
            ImmutableArray<SchedulerScheduleDispatch>.Empty);

    public SchedulerCoordinatorState WithPendingDispatch(SchedulerScheduleDispatch dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        return new SchedulerCoordinatorState(
            RegistryVersion,
            RegistryFingerprint,
            Materializations,
            LastReconciliationDiff,
            PendingDispatches.Add(dispatch));
    }
}

internal sealed record SchedulerReconciliationResult
{
    private SchedulerReconciliationResult(
        ImmutableArray<SchedulerScheduleMaterialization> materializations,
        ImmutableArray<RegistryScheduleLoadError> errors,
        SchedulerScheduleReconciliationDiff diff)
    {
        Materializations = materializations;
        Errors = errors;
        Diff = diff;
    }

    public bool IsAccepted => Errors.IsEmpty;

    public ImmutableArray<SchedulerScheduleMaterialization> Materializations { get; }

    public ImmutableArray<RegistryScheduleLoadError> Errors { get; }

    public SchedulerScheduleReconciliationDiff Diff { get; }

    public static SchedulerReconciliationResult Accepted(
        ImmutableArray<SchedulerScheduleMaterialization> materializations,
        SchedulerScheduleReconciliationDiff diff) =>
        new(
            materializations,
            ImmutableArray<RegistryScheduleLoadError>.Empty,
            diff ?? throw new ArgumentNullException(nameof(diff)));

    public static SchedulerReconciliationResult Rejected(
        ImmutableArray<RegistryScheduleLoadError> errors,
        SchedulerScheduleReconciliationDiff diff) =>
        new(
            ImmutableArray<SchedulerScheduleMaterialization>.Empty,
            errors,
            diff ?? throw new ArgumentNullException(nameof(diff)));
}

internal sealed record SchedulerDispatchResult
{
    private SchedulerDispatchResult(
        SchedulerScheduleDispatch? dispatch,
        SchedulerDispatchError? error)
    {
        Dispatch = dispatch;
        Error = error;
    }

    public bool IsAccepted => Error is null;

    public SchedulerScheduleDispatch? Dispatch { get; }

    public SchedulerDispatchError? Error { get; }

    public static SchedulerDispatchResult Accepted(SchedulerScheduleDispatch dispatch) =>
        new(dispatch ?? throw new ArgumentNullException(nameof(dispatch)), error: null);

    public static SchedulerDispatchResult Rejected(SchedulerDispatchError error) =>
        new(dispatch: null, error ?? throw new ArgumentNullException(nameof(error)));
}

internal sealed record SchedulerDispatchError(string Code, string Message)
{
    public override string ToString() => $"{Code}: {Message}";
}

internal interface ISchedulerQuartzAdapter
{
    void Schedule(IActorContext context, IActorRef receiver, SchedulerQuartzJob job);

    void Unschedule(IActorContext context, SchedulerScheduleKey key);
}

internal sealed record SchedulerQuartzJob
{
    public SchedulerQuartzJob(
        SchedulerScheduleKey key,
        DomainCronExpression cron,
        string timeZone)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Identity = SchedulerQuartzIdentity.From(key);
        Cron = cron ?? throw new ArgumentNullException(nameof(cron));
        TimeZone = RequireToken(timeZone, nameof(timeZone));
    }

    public SchedulerScheduleKey Key { get; }

    public SchedulerQuartzIdentity Identity { get; }

    public DomainCronExpression Cron { get; }

    public string TimeZone { get; }

    private static string RequireToken(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty or whitespace.", parameterName);
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Value cannot contain leading or trailing whitespace.", parameterName);
        }

        return value;
    }
}

internal sealed class NoopSchedulerQuartzAdapter : ISchedulerQuartzAdapter
{
    public static NoopSchedulerQuartzAdapter Instance { get; } = new();

    private NoopSchedulerQuartzAdapter()
    {
    }

    public void Schedule(IActorContext context, IActorRef receiver, SchedulerQuartzJob job)
    {
    }

    public void Unschedule(IActorContext context, SchedulerScheduleKey key)
    {
    }
}

internal sealed class AkkaQuartzSchedulerAdapter : ISchedulerQuartzAdapter
{
    public void Schedule(IActorContext context, IActorRef receiver, SchedulerQuartzJob job)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(job);

        var quartzActor = GetOrCreateQuartzActor(context);
        var trigger = BuildTrigger(job);

        quartzActor.Tell(new CreateJob(
            receiver,
            new SchedulerQuartzScheduleFired(job.Key),
            trigger));
    }

    public void Unschedule(IActorContext context, SchedulerScheduleKey key)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(key);

        var quartzActor = GetOrCreateQuartzActor(context);
        var identity = SchedulerQuartzIdentity.From(key);

        quartzActor.Tell(new RemoveJob(
            new JobKey(identity.JobName, identity.JobGroup),
            new TriggerKey(identity.TriggerName, identity.TriggerGroup)));
    }

    internal static ITrigger BuildTrigger(SchedulerQuartzJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return TriggerBuilder
            .Create()
            .WithIdentity(job.Identity.TriggerName, job.Identity.TriggerGroup)
            .ForJob(job.Identity.JobName, job.Identity.JobGroup)
            .WithCronSchedule(
                job.Cron.Value,
                schedule => schedule.InTimeZone(TimeZoneInfo.FindSystemTimeZoneById(job.TimeZone)))
            .Build();
    }

    private static IActorRef GetOrCreateQuartzActor(IActorContext context)
    {
        var child = context.Child(SchedulerCoordinatorIdentity.QuartzActorName);
        return child.Equals(ActorRefs.Nobody)
            ? context.ActorOf(
                Akka.Actor.Props.Create(() => new HiveQuartzActor()),
                SchedulerCoordinatorIdentity.QuartzActorName)
            : child;
    }
}

internal sealed class HiveQuartzActor : QuartzActor
{
    public HiveQuartzActor()
    {
    }

    public HiveQuartzActor(Quartz.IScheduler scheduler)
        : base(scheduler)
    {
    }

    protected override void CreateJobCommand(CreateJob createJob)
    {
        var sender = Context.Sender;
        ActorTaskScheduler.RunTask(async () =>
        {
            if (createJob.To is null)
            {
                sender.Tell(new CreateJobFail(null!, null!, new ArgumentNullException(nameof(createJob.To))));
                return;
            }

            if (createJob.Trigger is null)
            {
                sender.Tell(new CreateJobFail(null!, null!, new ArgumentNullException(nameof(createJob.Trigger))));
                return;
            }

            try
            {
                var jobDetail = QuartzJob
                    .CreateBuilderWithData(createJob.To, createJob.Message)
                    .WithIdentity(createJob.Trigger.JobKey)
                    .Build();

                await Scheduler.ScheduleJob(
                    jobDetail,
                    new[] { createJob.Trigger },
                    replace: true,
                    CancellationToken.None);

                sender.Tell(new JobCreated(createJob.Trigger.JobKey, createJob.Trigger.Key));
            }
            catch (Exception reason)
            {
                sender.Tell(new CreateJobFail(createJob.Trigger.JobKey, createJob.Trigger.Key, reason));
            }
        });
    }

    protected override void RemoveJobCommand(RemoveJob removeJob)
    {
        var sender = Context.Sender;
        ActorTaskScheduler.RunTask(async () =>
        {
            if (removeJob.JobKey is null)
            {
                sender.Tell(new RemoveJobFail(
                    null!,
                    removeJob.TriggerKey,
                    new ArgumentNullException(nameof(removeJob.JobKey))));
                return;
            }

            if (removeJob.TriggerKey is null)
            {
                sender.Tell(new RemoveJobFail(
                    removeJob.JobKey,
                    null!,
                    new ArgumentNullException(nameof(removeJob.TriggerKey))));
                return;
            }

            try
            {
                await Scheduler.UnscheduleJob(removeJob.TriggerKey, CancellationToken.None);
                await Scheduler.DeleteJob(removeJob.JobKey, CancellationToken.None);

                sender.Tell(new JobRemoved(removeJob.JobKey, removeJob.TriggerKey));
            }
            catch (Exception reason)
            {
                sender.Tell(new RemoveJobFail(removeJob.JobKey, removeJob.TriggerKey, reason));
            }
        });
    }
}
