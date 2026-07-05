using System.Collections.Immutable;
using System.Text;
using Akka.Actor;
using Akka.Dispatch;
using Akka.Quartz.Actor;
using Akka.Quartz.Actor.Commands;
using Akka.Quartz.Actor.Events;
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

        // Diagnostic identity probe (US-F0-09-T03c): reveals the active singleton instance by
        // replying with its own IActorRef. Sent through the ClusterSingletonProxy it lets callers
        // (and tests) locate which node currently hosts the single active coordinator, proving that
        // startup and post-handover restart both keep exactly one active instance.
        Receive<WhereIsSchedulerCoordinator>(_ => Sender.Tell(Self));
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
        var materialization = _state.Materializations.FirstOrDefault(materialization => materialization.Key == key);
        if (materialization is null)
        {
            Sender.Tell(SchedulerDispatchResult.Rejected(new SchedulerDispatchError(
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
            Sender.Tell(SchedulerDispatchResult.Rejected(error!));
            return;
        }

        var dispatch = new SchedulerScheduleDispatch(
            key,
            firedAtUtc,
            dispatchWindow!.Window,
            dispatchWindow.IdempotencyKey);
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
    public SchedulerScheduleDispatch(
        SchedulerScheduleKey key,
        DateTimeOffset firedAtUtc,
        TemporalWindow window,
        PulseIdempotencyKey idempotencyKey)
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
    }

    public SchedulerScheduleKey Key { get; }

    public DateTimeOffset FiredAtUtc { get; }

    public TemporalWindow Window { get; }

    public PulseIdempotencyKey IdempotencyKey { get; }
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
}
