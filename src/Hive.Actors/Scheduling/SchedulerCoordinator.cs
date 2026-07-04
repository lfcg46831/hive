using System.Collections.Immutable;
using Akka.Actor;
using Akka.Quartz.Actor;
using Akka.Quartz.Actor.Commands;
using Hive.Domain.Identity;
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
}

internal sealed class SchedulerCoordinator : ReceiveActor
{
    private readonly ISchedulerQuartzAdapter _quartzAdapter;
    private readonly TimeProvider _clock;
    private SchedulerCoordinatorState _state = SchedulerCoordinatorState.Empty;

    public SchedulerCoordinator()
        : this(new AkkaQuartzSchedulerAdapter(), TimeProvider.System)
    {
    }

    public SchedulerCoordinator(ISchedulerQuartzAdapter quartzAdapter, TimeProvider clock)
    {
        _quartzAdapter = quartzAdapter ?? throw new ArgumentNullException(nameof(quartzAdapter));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        Receive<ReconcileSchedulerSchedules>(Handle);
        Receive<DispatchSchedulerSchedule>(Handle);
        Receive<SchedulerQuartzScheduleFired>(Handle);
        Receive<GetSchedulerCoordinatorState>(_ => Sender.Tell(_state));
    }

    public static Props Props() => Akka.Actor.Props.Create(() => new SchedulerCoordinator());

    public static Props Props(ISchedulerQuartzAdapter quartzAdapter, TimeProvider clock) =>
        Akka.Actor.Props.Create(() => new SchedulerCoordinator(quartzAdapter, clock));

    private void Handle(ReconcileSchedulerSchedules command)
    {
        var loaded = RegistryScheduleLoader.Load(command.Snapshot);
        if (!loaded.IsValid)
        {
            Sender.Tell(SchedulerReconciliationResult.Rejected(loaded.Errors));
            return;
        }

        var materializations = loaded.Schedules
            .Where(schedule => schedule.IsActive)
            .OrderBy(schedule => schedule.OrganizationId.Value, StringComparer.Ordinal)
            .ThenBy(schedule => schedule.PositionId.Value, StringComparer.Ordinal)
            .ThenBy(schedule => schedule.Definition.Id.Value, StringComparer.Ordinal)
            .Select(schedule => new SchedulerScheduleMaterialization(
                SchedulerScheduleKey.From(
                    schedule.OrganizationId,
                    schedule.PositionId,
                    schedule.Definition.Id),
                schedule.Definition,
                schedule.WorkingHours))
            .ToImmutableArray();

        _state = _state.WithMaterializations(
            command.Snapshot.Version,
            command.Snapshot.Fingerprint,
            materializations);

        foreach (var materialization in materializations)
        {
            _quartzAdapter.Schedule(
                Context,
                Self,
                new SchedulerQuartzJob(
                    materialization.Key,
                    materialization.Definition.Cron,
                    materialization.Definition.TimeZone));
        }

        Sender.Tell(SchedulerReconciliationResult.Accepted(materializations));
    }

    private void Handle(DispatchSchedulerSchedule command)
    {
        Dispatch(command.Key, command.FiredAtUtc);
    }

    private void Handle(SchedulerQuartzScheduleFired command)
    {
        Dispatch(command.Key, _clock.GetUtcNow());
    }

    private void Dispatch(SchedulerScheduleKey key, DateTimeOffset firedAtUtc)
    {
        if (!_state.Materializations.Any(materialization => materialization.Key == key))
        {
            Sender.Tell(SchedulerDispatchResult.Rejected(new SchedulerDispatchError(
                "schedule-not-materialized",
                $"Schedule '{key.Value}' is not materialized by the coordinator.")));
            return;
        }

        var dispatch = new SchedulerScheduleDispatch(key, firedAtUtc);
        _state = _state.WithPendingDispatch(dispatch);
        Sender.Tell(SchedulerDispatchResult.Accepted(dispatch));
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

internal sealed record SchedulerScheduleMaterialization
{
    public SchedulerScheduleMaterialization(
        SchedulerScheduleKey key,
        ScheduleDefinition definition,
        LoadedScheduleWorkingHours workingHours)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        WorkingHours = workingHours ?? throw new ArgumentNullException(nameof(workingHours));
    }

    public SchedulerScheduleKey Key { get; }

    public ScheduleDefinition Definition { get; }

    public LoadedScheduleWorkingHours WorkingHours { get; }
}

internal sealed record SchedulerScheduleDispatch
{
    public SchedulerScheduleDispatch(SchedulerScheduleKey key, DateTimeOffset firedAtUtc)
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

internal sealed record SchedulerCoordinatorState
{
    private SchedulerCoordinatorState(
        long? registryVersion,
        string? registryFingerprint,
        ImmutableArray<SchedulerScheduleMaterialization> materializations,
        ImmutableArray<SchedulerScheduleDispatch> pendingDispatches)
    {
        RegistryVersion = registryVersion;
        RegistryFingerprint = registryFingerprint;
        Materializations = materializations;
        PendingDispatches = pendingDispatches;
    }

    public static SchedulerCoordinatorState Empty { get; } = new(
        registryVersion: null,
        registryFingerprint: null,
        ImmutableArray<SchedulerScheduleMaterialization>.Empty,
        ImmutableArray<SchedulerScheduleDispatch>.Empty);

    public long? RegistryVersion { get; }

    public string? RegistryFingerprint { get; }

    public ImmutableArray<SchedulerScheduleMaterialization> Materializations { get; }

    public ImmutableArray<SchedulerScheduleDispatch> PendingDispatches { get; }

    public SchedulerCoordinatorState WithMaterializations(
        long registryVersion,
        string registryFingerprint,
        ImmutableArray<SchedulerScheduleMaterialization> materializations) =>
        new(
            registryVersion,
            registryFingerprint ?? throw new ArgumentNullException(nameof(registryFingerprint)),
            materializations,
            ImmutableArray<SchedulerScheduleDispatch>.Empty);

    public SchedulerCoordinatorState WithPendingDispatch(SchedulerScheduleDispatch dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        return new SchedulerCoordinatorState(
            RegistryVersion,
            RegistryFingerprint,
            Materializations,
            PendingDispatches.Add(dispatch));
    }
}

internal sealed record SchedulerReconciliationResult
{
    private SchedulerReconciliationResult(
        ImmutableArray<SchedulerScheduleMaterialization> materializations,
        ImmutableArray<RegistryScheduleLoadError> errors)
    {
        Materializations = materializations;
        Errors = errors;
    }

    public bool IsAccepted => Errors.IsEmpty;

    public ImmutableArray<SchedulerScheduleMaterialization> Materializations { get; }

    public ImmutableArray<RegistryScheduleLoadError> Errors { get; }

    public static SchedulerReconciliationResult Accepted(
        ImmutableArray<SchedulerScheduleMaterialization> materializations) =>
        new(materializations, ImmutableArray<RegistryScheduleLoadError>.Empty);

    public static SchedulerReconciliationResult Rejected(
        ImmutableArray<RegistryScheduleLoadError> errors) =>
        new(ImmutableArray<SchedulerScheduleMaterialization>.Empty, errors);
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
}

internal sealed record SchedulerQuartzJob
{
    public SchedulerQuartzJob(
        SchedulerScheduleKey key,
        DomainCronExpression cron,
        string timeZone)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Cron = cron ?? throw new ArgumentNullException(nameof(cron));
        TimeZone = RequireToken(timeZone, nameof(timeZone));
    }

    public SchedulerScheduleKey Key { get; }

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
}

internal sealed class AkkaQuartzSchedulerAdapter : ISchedulerQuartzAdapter
{
    public void Schedule(IActorContext context, IActorRef receiver, SchedulerQuartzJob job)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(job);

        var quartzActor = GetOrCreateQuartzActor(context);
        var trigger = TriggerBuilder
            .Create()
            .WithCronSchedule(
                job.Cron.Value,
                schedule => schedule.InTimeZone(TimeZoneInfo.FindSystemTimeZoneById(job.TimeZone)))
            .Build();

        quartzActor.Tell(new CreateJob(
            receiver,
            new SchedulerQuartzScheduleFired(job.Key),
            trigger));
    }

    private static IActorRef GetOrCreateQuartzActor(IActorContext context)
    {
        var child = context.Child(SchedulerCoordinatorIdentity.QuartzActorName);
        return child.Equals(ActorRefs.Nobody)
            ? context.ActorOf(
                Akka.Actor.Props.Create(() => new QuartzActor()),
                SchedulerCoordinatorIdentity.QuartzActorName)
            : child;
    }
}
