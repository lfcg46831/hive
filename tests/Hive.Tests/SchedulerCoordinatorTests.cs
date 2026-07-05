using System.Collections.Specialized;
using Akka.Actor;
using Akka.Quartz.Actor.Commands;
using Akka.Quartz.Actor.Events;
using Hive.Actors.Scheduling;
using Hive.Actors.Sharding;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using Hive.Domain.Scheduling;
using Hive.Infrastructure.Organization.Configuration;
using Hive.Infrastructure.Organization.Registry;
using Hive.Infrastructure.Scheduling;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Matchers;
using DomainCronExpression = Hive.Domain.Scheduling.CronExpression;

namespace Hive.Tests;

public sealed class SchedulerCoordinatorTests
{
    private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset ImportAt =
        new(2026, 7, 4, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Coordinator_identity_and_schedule_key_are_stable()
    {
        Assert.Equal("scheduler-coordinator", SchedulerCoordinatorIdentity.LogicalName);
        Assert.Equal("scheduler-coordinator", SchedulerCoordinatorIdentity.ActorName);

        var key = SchedulerScheduleKey.From(
            OrganizationId.From("acme-delivery"),
            PositionId.From("delivery-lead"),
            ScheduleId.From("daily-report"));

        Assert.Equal("acme-delivery/delivery-lead/daily-report", key.Value);
    }

    [Fact]
    public void Quartz_identity_is_derived_deterministically_from_schedule_key()
    {
        var key = SchedulerScheduleKey.From(
            OrganizationId.From("acme-delivery"),
            PositionId.From("delivery-lead"),
            ScheduleId.From("daily-report"));

        var identity = SchedulerQuartzIdentity.From(key);

        Assert.Equal("hive-scheduler-jobs", identity.JobGroup);
        Assert.Equal("job--acme-delivery--delivery-lead--daily-report", identity.JobName);
        Assert.Equal("hive-scheduler-triggers", identity.TriggerGroup);
        Assert.Equal("trigger--acme-delivery--delivery-lead--daily-report", identity.TriggerName);
    }

    [Fact]
    public void Quartz_identity_sanitizes_non_token_characters_deterministically()
    {
        var key = SchedulerScheduleKey.From(
            OrganizationId.From("Acme Org"),
            PositionId.From("delivery_lead"),
            ScheduleId.From("daily:report"));

        var identity = SchedulerQuartzIdentity.From(key);

        Assert.Equal(
            "job--Acme_u0020_Org--delivery_u005f_lead--daily_u003a_report",
            identity.JobName);
        Assert.Equal(
            "trigger--Acme_u0020_Org--delivery_u005f_lead--daily_u003a_report",
            identity.TriggerName);
    }

    [Fact]
    public void Schedule_window_calculator_maps_observed_fire_to_canonical_cron_window()
    {
        var materialization = Materialization(
            "daily-report",
            "0 55 17 ? * MON-FRI",
            "Europe/Lisbon");
        var firedAtUtc = new DateTimeOffset(2026, 7, 3, 17, 10, 0, TimeSpan.Zero);

        var dispatchWindow = SchedulerScheduleWindowCalculator.Calculate(
            materialization,
            firedAtUtc);

        Assert.Equal(new DateTimeOffset(2026, 7, 3, 16, 55, 0, TimeSpan.Zero), dispatchWindow.Window.Start);
        Assert.Equal(new DateTimeOffset(2026, 7, 6, 16, 55, 0, TimeSpan.Zero), dispatchWindow.Window.End);
        Assert.Equal(
            "acme-delivery/delivery-lead/daily-report/2026-07-03T16:55:00.0000000Z/2026-07-06T16:55:00.0000000Z",
            dispatchWindow.IdempotencyKey.Value);
    }

    [Fact]
    public void Schedule_window_calculator_is_stable_within_window_and_changes_for_next_window()
    {
        var materialization = Materialization(
            "daily-report",
            "0 55 17 ? * MON-FRI",
            "Europe/Lisbon");

        var first = SchedulerScheduleWindowCalculator.Calculate(
            materialization,
            new DateTimeOffset(2026, 7, 3, 17, 10, 0, TimeSpan.Zero));
        var repeated = SchedulerScheduleWindowCalculator.Calculate(
            materialization,
            new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero));
        var next = SchedulerScheduleWindowCalculator.Calculate(
            materialization,
            new DateTimeOffset(2026, 7, 6, 17, 0, 0, TimeSpan.Zero));

        Assert.Equal(first.IdempotencyKey, repeated.IdempotencyKey);
        Assert.Equal(first.Window, repeated.Window);
        Assert.NotEqual(first.IdempotencyKey, next.IdempotencyKey);
    }

    [Fact]
    public void Schedule_window_calculator_respects_schedule_timezone()
    {
        var lisbon = Materialization(
            "morning-report",
            "0 0 9 ? * MON-FRI",
            "Europe/Lisbon");
        var newYork = Materialization(
            "morning-report",
            "0 0 9 ? * MON-FRI",
            "America/New_York");

        var lisbonWindow = SchedulerScheduleWindowCalculator.Calculate(
            lisbon,
            new DateTimeOffset(2026, 7, 3, 8, 1, 0, TimeSpan.Zero));
        var newYorkWindow = SchedulerScheduleWindowCalculator.Calculate(
            newYork,
            new DateTimeOffset(2026, 7, 3, 13, 1, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 7, 3, 8, 0, 0, TimeSpan.Zero), lisbonWindow.Window.Start);
        Assert.Equal(new DateTimeOffset(2026, 7, 3, 13, 0, 0, TimeSpan.Zero), newYorkWindow.Window.Start);
    }

    [Fact]
    public void Schedule_window_calculator_rejects_when_no_canonical_window_can_be_resolved()
    {
        var materialization = Materialization(
            "future-report",
            "0 0 9 ? * MON-FRI 2099",
            "Europe/Lisbon");

        var calculated = SchedulerScheduleWindowCalculator.TryCalculate(
            materialization,
            new DateTimeOffset(2026, 7, 3, 8, 1, 0, TimeSpan.Zero),
            out var dispatchWindow,
            out var error);

        Assert.False(calculated);
        Assert.Null(dispatchWindow);
        Assert.Equal("schedule-window-unresolved", error?.Code);
    }

    [Fact]
    public void Missed_window_evaluator_decides_from_latest_declared_window_and_existing_delivery_state()
    {
        var nowUtc = new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);
        var nonCritical = Materialization(
            "daily-report",
            "0 55 17 ? * MON-FRI",
            "Europe/Lisbon",
            declaredAtUtc: new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero));

        var resolved = SchedulerMissedWindowEvaluator.TryResolveCandidate(
            nonCritical,
            nowUtc,
            out var candidate,
            out var error);

        Assert.True(resolved, error?.ToString());
        Assert.NotNull(candidate);
        Assert.Equal(
            "acme-delivery/delivery-lead/daily-report/2026-07-03T16:55:00.0000000Z/2026-07-06T16:55:00.0000000Z",
            candidate.DispatchWindow.IdempotencyKey.Value);

        var skip = SchedulerMissedWindowEvaluator.Decide(
            nonCritical,
            candidate,
            existingDelivery: null);

        Assert.Equal(SchedulerMissedWindowAction.Skip, skip.Action);
        Assert.Equal("scheduler-missed-window-skipped", skip.Reason?.Code);

        var existing = new SchedulerPulseDeliveryState(
            candidate.DispatchWindow.IdempotencyKey,
            MessageId.From(Guid.Parse("11111111-1111-4111-8111-111111111111")),
            ThreadId.From(Guid.Parse("22222222-2222-4222-8222-222222222222")),
            SchedulerPulseDeliveryStatus.Delivered,
            attemptCount: 1,
            nowUtc,
            reason: null);

        var noAction = SchedulerMissedWindowEvaluator.Decide(
            nonCritical,
            candidate,
            existing);

        Assert.Equal(SchedulerMissedWindowAction.None, noAction.Action);

        var criticalCatchUp = Materialization(
            "critical-report",
            "0 55 17 ? * MON-FRI",
            "Europe/Lisbon",
            isCritical: true,
            catchUp: CatchUpPolicy.CatchUpOnce,
            declaredAtUtc: new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero));
        var criticalResolved = SchedulerMissedWindowEvaluator.TryResolveCandidate(
            criticalCatchUp,
            nowUtc,
            out var criticalCandidate,
            out var criticalError);
        Assert.True(criticalResolved, criticalError?.ToString());

        var catchUp = SchedulerMissedWindowEvaluator.Decide(
            criticalCatchUp,
            criticalCandidate!,
            existingDelivery: null);

        Assert.Equal(SchedulerMissedWindowAction.CatchUp, catchUp.Action);
        Assert.Null(catchUp.Reason);

        var criticalSkip = Materialization(
            "critical-skip-report",
            "0 55 17 ? * MON-FRI",
            "Europe/Lisbon",
            isCritical: true,
            declaredAtUtc: new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero));
        var criticalSkipResolved = SchedulerMissedWindowEvaluator.TryResolveCandidate(
            criticalSkip,
            nowUtc,
            out var criticalSkipCandidate,
            out var criticalSkipError);
        Assert.True(criticalSkipResolved, criticalSkipError?.ToString());

        var criticalSkipDecision = SchedulerMissedWindowEvaluator.Decide(
            criticalSkip,
            criticalSkipCandidate!,
            existingDelivery: null);

        Assert.Equal(SchedulerMissedWindowAction.Skip, criticalSkipDecision.Action);

        var declaredAfterLatestWindow = Materialization(
            "new-report",
            "0 55 17 ? * MON-FRI",
            "Europe/Lisbon",
            declaredAtUtc: new DateTimeOffset(2026, 7, 4, 9, 0, 0, TimeSpan.Zero));
        var lateResolved = SchedulerMissedWindowEvaluator.TryResolveCandidate(
            declaredAfterLatestWindow,
            nowUtc,
            out var lateCandidate,
            out var lateError);

        Assert.False(lateResolved);
        Assert.Null(lateCandidate);
        Assert.Null(lateError);
    }

    [Fact]
    public void Scheduler_pulse_factory_builds_canonical_pulse_from_dispatch_metadata()
    {
        var materialization = Materialization(
            "daily-report",
            "0 55 17 ? * MON-FRI",
            "Europe/Lisbon");
        var firedAtUtc = new DateTimeOffset(2026, 7, 3, 17, 10, 0, TimeSpan.Zero);
        var dispatchWindow = SchedulerScheduleWindowCalculator.Calculate(
            materialization,
            firedAtUtc);

        var pulse = BuildSchedulerPulse(
            materialization,
            firedAtUtc,
            dispatchWindow.IdempotencyKey);

        Assert.Equal(materialization.Key.Organization, pulse.OrganizationId);
        var from = Assert.IsType<SystemEndpointRef>(pulse.From);
        Assert.Equal(SystemEndpointKind.Scheduler, from.Kind);
        var to = Assert.IsType<PositionEndpointRef>(pulse.To);
        Assert.Equal(materialization.Key.Position, to.PositionId);
        Assert.Equal("daily-report", pulse.ScheduleId);
        Assert.Equal("Run scheduled work", pulse.Payload);
        Assert.Equal(Priority.Normal, pulse.Priority);
        Assert.Equal(1, pulse.SchemaVersion);
        Assert.Equal(firedAtUtc, pulse.SentAt);
        Assert.Null(pulse.Deadline);
        Assert.NotEqual(Guid.Empty, pulse.Id.Value);
        Assert.NotEqual(Guid.Empty, pulse.Thread.Value);
        Assert.NotEqual(pulse.Id.Value, pulse.Thread.Value);

        var repeatedPulse = BuildSchedulerPulse(
            materialization,
            new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero),
            dispatchWindow.IdempotencyKey);
        Assert.Equal(pulse.Id, repeatedPulse.Id);
        Assert.Equal(pulse.Thread, repeatedPulse.Thread);

        var nextDispatchWindow = SchedulerScheduleWindowCalculator.Calculate(
            materialization,
            new DateTimeOffset(2026, 7, 6, 17, 0, 0, TimeSpan.Zero));
        var nextPulse = BuildSchedulerPulse(
            materialization,
            new DateTimeOffset(2026, 7, 6, 17, 0, 0, TimeSpan.Zero),
            nextDispatchWindow.IdempotencyKey);
        Assert.NotEqual(pulse.Id, nextPulse.Id);
        Assert.NotEqual(pulse.Thread, nextPulse.Thread);
    }

    [Fact]
    public void Scheduler_pulse_dispatcher_wraps_pulse_for_position_sharding()
    {
        var materialization = Materialization(
            "daily-report",
            "0 55 17 ? * MON-FRI",
            "Europe/Lisbon");
        var firedAtUtc = new DateTimeOffset(2026, 7, 3, 17, 10, 0, TimeSpan.Zero);
        var dispatchWindow = SchedulerScheduleWindowCalculator.Calculate(
            materialization,
            firedAtUtc);
        var pulse = BuildSchedulerPulse(
            materialization,
            firedAtUtc,
            dispatchWindow.IdempotencyKey);

        var envelope = AkkaClusterShardingSchedulerPulseDispatcher.ToEnvelope(pulse);

        Assert.Equal(
            PositionEntityId.From(materialization.Key.Organization, materialization.Key.Position),
            envelope.Position);
        var command = Assert.IsType<AcceptMessage>(envelope.Command);
        Assert.Same(pulse, command.Message);
    }

    [Fact]
    public void Scheduler_dispatch_policy_uses_position_timezone_and_exclusive_working_hours()
    {
        var atStartMaterialization = Materialization(
            "start-report",
            "0 0 9 ? * MON-FRI",
            "Europe/Lisbon");
        var atStartDispatch = BuildDispatch(
            atStartMaterialization,
            new DateTimeOffset(2026, 7, 3, 8, 0, 0, TimeSpan.Zero));

        var atStartDecision = SchedulerDispatchPolicy.Evaluate(
            atStartMaterialization,
            atStartDispatch,
            hasAvailableProactiveBudget: true);

        Assert.True(atStartDecision.IsAllowed, atStartDecision.Reason?.Code);

        var atEndMaterialization = Materialization(
            "end-report",
            "0 0 18 ? * MON-FRI",
            "Europe/Lisbon");
        var atEndDispatch = BuildDispatch(
            atEndMaterialization,
            new DateTimeOffset(2026, 7, 3, 17, 0, 0, TimeSpan.Zero));

        var atEndDecision = SchedulerDispatchPolicy.Evaluate(
            atEndMaterialization,
            atEndDispatch,
            hasAvailableProactiveBudget: true);

        Assert.False(atEndDecision.IsAllowed);
        Assert.Equal("scheduler-outside-working-hours", atEndDecision.Reason?.Code);
    }

    [Fact]
    public async Task Reconcile_materializes_only_active_schedules_in_deterministic_order()
    {
        var snapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "zeta-report",
                "0 55 17 ? * MON-FRI",
                "Run zeta report"),
            new ScheduleEntryConfiguration(
                "paused-report",
                "0 0 12 ? * MON-FRI",
                "Do not materialize this",
                isActive: false),
            new ScheduleEntryConfiguration(
                "alpha-report",
                "0 0 9 ? * MON-FRI",
                "Run alpha report")));
        var system = ActorSystem.Create("scheduler-coordinator-reconcile");
        try
        {
            var coordinator = system.ActorOf(
                ProtocolOnlyCoordinatorProps(),
                "scheduler-coordinator-reconcile");

            var result = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(snapshot),
                AskTimeout);

            Assert.True(result.IsAccepted, string.Join(Environment.NewLine, result.Errors));
            Assert.Equal(
                new[]
                {
                    "acme-delivery/delivery-lead/alpha-report",
                    "acme-delivery/delivery-lead/zeta-report",
                },
                result.Materializations.Select(materialization => materialization.Key.Value));

            var alpha = result.Materializations[0];
            Assert.Equal("0 0 9 ? * MON-FRI", alpha.Definition.Cron.Value);
            Assert.Equal("Run alpha report", alpha.Definition.Payload);
            Assert.Equal(new TimeOnly(9, 0), alpha.WorkingHours.Start);
            Assert.Equal(new TimeOnly(18, 0), alpha.WorkingHours.End);

            var state = await coordinator.Ask<SchedulerCoordinatorState>(
                GetSchedulerCoordinatorState.Instance,
                AskTimeout);

            Assert.Equal(
                result.Materializations.Select(materialization => materialization.Key.Value),
                state.Materializations.Select(materialization => materialization.Key.Value));
            Assert.Empty(state.PendingDispatches);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Reconcile_schedules_active_materializations_through_quartz_adapter()
    {
        var snapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "zeta-report",
                "0 55 17 ? * MON-FRI",
                "Run zeta report"),
            new ScheduleEntryConfiguration(
                "paused-report",
                "0 0 12 ? * MON-FRI",
                "Do not schedule this",
                isActive: false),
            new ScheduleEntryConfiguration(
                "alpha-report",
                "0 0 9 ? * MON-FRI",
                "Run alpha report")));
        var quartz = new RecordingSchedulerQuartzAdapter();
        var system = ActorSystem.Create("scheduler-coordinator-quartz");
        try
        {
            var coordinator = system.ActorOf(
                SchedulerCoordinator.Props(quartz, TimeProvider.System),
                "scheduler-coordinator-quartz");

            var result = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(snapshot),
                AskTimeout);

            Assert.True(result.IsAccepted, string.Join(Environment.NewLine, result.Errors));
            Assert.Equal(
                new[]
                {
                    "acme-delivery/delivery-lead/alpha-report",
                    "acme-delivery/delivery-lead/zeta-report",
                },
                quartz.Jobs.Select(job => job.Key.Value));
            Assert.Equal(
                new[]
                {
                    "0 0 9 ? * MON-FRI",
                    "0 55 17 ? * MON-FRI",
                },
                quartz.Jobs.Select(job => job.Cron.Value));
            Assert.All(quartz.Jobs, job => Assert.Equal("Europe/Lisbon", job.TimeZone));
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Reconcile_same_snapshot_keeps_single_logical_quartz_job_per_identity()
    {
        var snapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "daily-report",
                "0 55 17 ? * MON-FRI",
                "Run daily report")));
        var quartz = new RecordingSchedulerQuartzAdapter();
        var system = ActorSystem.Create("scheduler-coordinator-quartz-idempotent");
        try
        {
            var coordinator = system.ActorOf(
                SchedulerCoordinator.Props(quartz, TimeProvider.System),
                "scheduler-coordinator-quartz-idempotent");

            var first = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(snapshot),
                AskTimeout);
            var second = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(snapshot),
                AskTimeout);

            Assert.True(first.IsAccepted, string.Join(Environment.NewLine, first.Errors));
            Assert.True(second.IsAccepted, string.Join(Environment.NewLine, second.Errors));
            var job = Assert.Single(quartz.Jobs);
            Assert.Equal(
                "job--acme-delivery--delivery-lead--daily-report",
                job.Identity.JobName);
            Assert.Equal(
                "trigger--acme-delivery--delivery-lead--daily-report",
                job.Identity.TriggerName);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Reconcile_changed_schedule_preserves_quartz_identity_and_updates_definition()
    {
        var initialSnapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "daily-report",
                "0 55 17 ? * MON-FRI",
                "Run daily report")));
        var updatedSnapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "daily-report",
                "0 0 18 ? * MON-FRI",
                "Run daily report")));
        var quartz = new RecordingSchedulerQuartzAdapter();
        var system = ActorSystem.Create("scheduler-coordinator-quartz-update");
        try
        {
            var coordinator = system.ActorOf(
                SchedulerCoordinator.Props(quartz, TimeProvider.System),
                "scheduler-coordinator-quartz-update");

            var first = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(initialSnapshot),
                AskTimeout);
            var second = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(updatedSnapshot),
                AskTimeout);

            Assert.True(first.IsAccepted, string.Join(Environment.NewLine, first.Errors));
            Assert.True(second.IsAccepted, string.Join(Environment.NewLine, second.Errors));
            var job = Assert.Single(quartz.Jobs);
            Assert.Equal(
                "job--acme-delivery--delivery-lead--daily-report",
                job.Identity.JobName);
            Assert.Equal("0 0 18 ? * MON-FRI", job.Cron.Value);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Reconcile_same_snapshot_reports_empty_diff_without_rescheduling()
    {
        var snapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "daily-report",
                "0 55 17 ? * MON-FRI",
                "Run daily report")));
        var quartz = new RecordingSchedulerQuartzAdapter();
        var system = ActorSystem.Create("scheduler-coordinator-reconcile-same-diff");
        try
        {
            var coordinator = system.ActorOf(
                SchedulerCoordinator.Props(quartz, TimeProvider.System),
                "scheduler-coordinator-reconcile-same-diff");

            var first = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(snapshot),
                AskTimeout);
            var second = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(snapshot),
                AskTimeout);

            Assert.True(first.IsAccepted, string.Join(Environment.NewLine, first.Errors));
            Assert.True(first.Diff.IsRegistryChanged);
            Assert.Equal(
                new[] { SchedulerScheduleReconciliationOperationKind.Create },
                first.Diff.Operations.Select(operation => operation.Kind));
            Assert.True(second.IsAccepted, string.Join(Environment.NewLine, second.Errors));
            Assert.False(second.Diff.IsRegistryChanged);
            Assert.Empty(second.Diff.Operations);
            Assert.Single(quartz.Jobs);
            Assert.Single(quartz.ScheduleCalls);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Reconcile_calculates_deterministic_diff_for_created_updated_paused_and_removed_schedules()
    {
        var initialSnapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "alpha-report",
                "0 0 10 ? * MON-FRI",
                "Run alpha report"),
            new ScheduleEntryConfiguration(
                "beta-report",
                "0 0 11 ? * MON-FRI",
                "Run beta report"),
            new ScheduleEntryConfiguration(
                "remove-report",
                "0 0 12 ? * MON-FRI",
                "Run removable report"),
            new ScheduleEntryConfiguration(
                "inactive-from-start",
                "0 0 13 ? * MON-FRI",
                "Keep inactive",
                isActive: false)));
        var updatedSnapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "alpha-report",
                "0 0 10 ? * MON-FRI",
                "Run alpha report",
                isActive: false),
            new ScheduleEntryConfiguration(
                "beta-report",
                "0 30 11 ? * MON-FRI",
                "Run changed beta report"),
            new ScheduleEntryConfiguration(
                "gamma-report",
                "0 0 14 ? * MON-FRI",
                "Run gamma report"),
            new ScheduleEntryConfiguration(
                "inactive-new",
                "0 0 15 ? * MON-FRI",
                "Still inactive",
                isActive: false)));
        var system = ActorSystem.Create("scheduler-coordinator-reconcile-diff");
        try
        {
            var coordinator = system.ActorOf(
                ProtocolOnlyCoordinatorProps(),
                "scheduler-coordinator-reconcile-diff");

            var first = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(initialSnapshot),
                AskTimeout);
            var second = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(updatedSnapshot),
                AskTimeout);

            Assert.True(first.IsAccepted, string.Join(Environment.NewLine, first.Errors));
            Assert.Equal(
                new[]
                {
                    "Create:acme-delivery/delivery-lead/alpha-report",
                    "Create:acme-delivery/delivery-lead/beta-report",
                    "Create:acme-delivery/delivery-lead/remove-report",
                },
                first.Diff.Operations.Select(DescribeOperation));

            Assert.True(second.IsAccepted, string.Join(Environment.NewLine, second.Errors));
            Assert.True(second.Diff.IsRegistryChanged);
            Assert.Equal(initialSnapshot.Version, second.Diff.PreviousRegistryVersion);
            Assert.Equal(initialSnapshot.Fingerprint, second.Diff.PreviousRegistryFingerprint);
            Assert.Equal(updatedSnapshot.Version, second.Diff.NewRegistryVersion);
            Assert.Equal(updatedSnapshot.Fingerprint, second.Diff.NewRegistryFingerprint);
            Assert.Equal(
                new[]
                {
                    "Pause:acme-delivery/delivery-lead/alpha-report",
                    "Update:acme-delivery/delivery-lead/beta-report",
                    "Create:acme-delivery/delivery-lead/gamma-report",
                    "Remove:acme-delivery/delivery-lead/remove-report",
                },
                second.Diff.Operations.Select(DescribeOperation));

            var betaUpdate = Assert.Single(second.Diff.Operations, operation =>
                operation.Kind == SchedulerScheduleReconciliationOperationKind.Update);
            Assert.Equal("0 0 11 ? * MON-FRI", betaUpdate.Current?.Definition.Cron.Value);
            Assert.Equal("0 30 11 ? * MON-FRI", betaUpdate.Declared?.Definition.Cron.Value);
            Assert.Equal("Run changed beta report", betaUpdate.Declared?.Definition.Payload);

            var state = await coordinator.Ask<SchedulerCoordinatorState>(
                GetSchedulerCoordinatorState.Instance,
                AskTimeout);
            Assert.Equal(
                new[]
                {
                    "acme-delivery/delivery-lead/beta-report",
                    "acme-delivery/delivery-lead/gamma-report",
                },
                state.Materializations.Select(materialization => materialization.Key.Value));
            Assert.Equal(second.Diff, state.LastReconciliationDiff);

            var paused = await coordinator.Ask<SchedulerDispatchResult>(
                new DispatchSchedulerSchedule(
                    SchedulerScheduleKey.From(
                        OrganizationId.From("acme-delivery"),
                        PositionId.From("delivery-lead"),
                        ScheduleId.From("alpha-report")),
                    new DateTimeOffset(2026, 7, 3, 10, 0, 0, TimeSpan.Zero)),
                AskTimeout);
            var removed = await coordinator.Ask<SchedulerDispatchResult>(
                new DispatchSchedulerSchedule(
                    SchedulerScheduleKey.From(
                        OrganizationId.From("acme-delivery"),
                        PositionId.From("delivery-lead"),
                        ScheduleId.From("remove-report")),
                    new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero)),
                AskTimeout);

            Assert.False(paused.IsAccepted);
            Assert.Equal("schedule-not-materialized", paused.Error?.Code);
            Assert.False(removed.IsAccepted);
            Assert.Equal("schedule-not-materialized", removed.Error?.Code);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Reconcile_applies_only_material_diff_operations_to_quartz_adapter()
    {
        var initialSnapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "alpha-report",
                "0 0 10 ? * MON-FRI",
                "Run alpha report"),
            new ScheduleEntryConfiguration(
                "beta-report",
                "0 0 11 ? * MON-FRI",
                "Run beta report"),
            new ScheduleEntryConfiguration(
                "remove-report",
                "0 0 12 ? * MON-FRI",
                "Run removable report"),
            new ScheduleEntryConfiguration(
                "stable-report",
                "0 0 15 ? * MON-FRI",
                "Run stable report")));
        var updatedSnapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "alpha-report",
                "0 0 10 ? * MON-FRI",
                "Run alpha report",
                isActive: false),
            new ScheduleEntryConfiguration(
                "beta-report",
                "0 30 11 ? * MON-FRI",
                "Run changed beta report"),
            new ScheduleEntryConfiguration(
                "gamma-report",
                "0 0 14 ? * MON-FRI",
                "Run gamma report"),
            new ScheduleEntryConfiguration(
                "stable-report",
                "0 0 15 ? * MON-FRI",
                "Run stable report")));
        var quartz = new RecordingSchedulerQuartzAdapter();
        var system = ActorSystem.Create("scheduler-coordinator-reconcile-quartz-diff");
        try
        {
            var coordinator = system.ActorOf(
                SchedulerCoordinator.Props(quartz, TimeProvider.System),
                "scheduler-coordinator-reconcile-quartz-diff");

            var first = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(initialSnapshot),
                AskTimeout);
            var initialScheduleCallCount = quartz.ScheduleCalls.Count;

            var second = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(updatedSnapshot),
                AskTimeout);
            var repeated = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(updatedSnapshot),
                AskTimeout);

            Assert.True(first.IsAccepted, string.Join(Environment.NewLine, first.Errors));
            Assert.True(second.IsAccepted, string.Join(Environment.NewLine, second.Errors));
            Assert.False(repeated.Diff.IsRegistryChanged);
            Assert.Equal(
                new[]
                {
                    "Update:acme-delivery/delivery-lead/beta-report",
                    "Create:acme-delivery/delivery-lead/gamma-report",
                },
                quartz.ScheduleCalls
                    .Skip(initialScheduleCallCount)
                    .Select(job => DescribeOperation(
                        job.Cron.Value == "0 30 11 ? * MON-FRI"
                            ? SchedulerScheduleReconciliationOperationKind.Update
                            : SchedulerScheduleReconciliationOperationKind.Create,
                        job.Key)));
            Assert.Equal(
                new[]
                {
                    "acme-delivery/delivery-lead/alpha-report",
                    "acme-delivery/delivery-lead/remove-report",
                },
                quartz.UnscheduleCalls.Select(key => key.Value));
            Assert.Equal(6, quartz.ScheduleCalls.Count);
            Assert.Equal(2, quartz.UnscheduleCalls.Count);
            Assert.Equal(
                new[]
                {
                    "acme-delivery/delivery-lead/beta-report",
                    "acme-delivery/delivery-lead/gamma-report",
                    "acme-delivery/delivery-lead/stable-report",
                },
                quartz.Jobs
                    .OrderBy(job => job.Key.Value, StringComparer.Ordinal)
                    .Select(job => job.Key.Value));
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Reconcile_audits_initial_changed_and_unchanged_configuration_snapshots()
    {
        var initialSnapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "alpha-report",
                "0 0 10 ? * MON-FRI",
                "Run alpha report"),
            new ScheduleEntryConfiguration(
                "beta-report",
                "0 0 11 ? * MON-FRI",
                "Run beta report"),
            new ScheduleEntryConfiguration(
                "remove-report",
                "0 0 12 ? * MON-FRI",
                "Run removable report"),
            new ScheduleEntryConfiguration(
                "stable-report",
                "0 0 15 ? * MON-FRI",
                "Run stable report")));
        var updatedSnapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "alpha-report",
                "0 0 10 ? * MON-FRI",
                "Run alpha report",
                isActive: false),
            new ScheduleEntryConfiguration(
                "beta-report",
                "0 30 11 ? * MON-FRI",
                "Run changed beta report"),
            new ScheduleEntryConfiguration(
                "gamma-report",
                "0 0 14 ? * MON-FRI",
                "Run gamma report"),
            new ScheduleEntryConfiguration(
                "stable-report",
                "0 0 15 ? * MON-FRI",
                "Run stable report")));
        var audit = new RecordingSchedulerReconciliationAuditSink();
        var system = ActorSystem.Create("scheduler-coordinator-reconcile-audit");
        try
        {
            var coordinator = system.ActorOf(
                SchedulerCoordinator.Props(
                    NoopSchedulerQuartzAdapter.Instance,
                    new ManualTimeProvider(ImportAt),
                    NoopSchedulerPulseDeliveryStore.Instance,
                    NoopSchedulerPulseDispatcher.Instance,
                    AllowingSchedulerProactiveBudgetPolicy.Instance,
                    audit),
                "scheduler-coordinator-reconcile-audit");

            var first = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(initialSnapshot),
                AskTimeout);
            var second = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(updatedSnapshot),
                AskTimeout);
            var repeated = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(updatedSnapshot),
                AskTimeout);

            Assert.True(first.IsAccepted, string.Join(Environment.NewLine, first.Errors));
            Assert.True(second.IsAccepted, string.Join(Environment.NewLine, second.Errors));
            Assert.True(repeated.IsAccepted, string.Join(Environment.NewLine, repeated.Errors));

            Assert.Equal(3, audit.Records.Count);

            var initialized = audit.Records[0];
            Assert.Equal(SchedulerReconciliationAuditOutcome.Accepted, initialized.Outcome);
            Assert.Equal("scheduler-configuration-initialized", initialized.Reason.Code);
            Assert.Null(initialized.PreviousRegistryVersion);
            Assert.Equal(initialSnapshot.Version, initialized.NewRegistryVersion);
            Assert.Equal(initialSnapshot.Fingerprint, initialized.NewRegistryFingerprint);
            Assert.Equal(
                new[]
                {
                    "Create:acme-delivery/delivery-lead/alpha-report",
                    "Create:acme-delivery/delivery-lead/beta-report",
                    "Create:acme-delivery/delivery-lead/remove-report",
                    "Create:acme-delivery/delivery-lead/stable-report",
                },
                initialized.Operations.Select(DescribeOperation));
            Assert.Empty(initialized.Errors);

            var changed = audit.Records[1];
            Assert.Equal(SchedulerReconciliationAuditOutcome.Accepted, changed.Outcome);
            Assert.Equal("scheduler-configuration-changed", changed.Reason.Code);
            Assert.Equal(initialSnapshot.Version, changed.PreviousRegistryVersion);
            Assert.Equal(initialSnapshot.Fingerprint, changed.PreviousRegistryFingerprint);
            Assert.Equal(updatedSnapshot.Version, changed.NewRegistryVersion);
            Assert.Equal(updatedSnapshot.Fingerprint, changed.NewRegistryFingerprint);
            Assert.Equal(
                new[]
                {
                    "Pause:acme-delivery/delivery-lead/alpha-report",
                    "Update:acme-delivery/delivery-lead/beta-report",
                    "Create:acme-delivery/delivery-lead/gamma-report",
                    "Remove:acme-delivery/delivery-lead/remove-report",
                    "Unchanged:acme-delivery/delivery-lead/stable-report",
                },
                changed.Operations.Select(DescribeOperation));
            Assert.Empty(changed.Errors);

            var unchanged = audit.Records[2];
            Assert.Equal(SchedulerReconciliationAuditOutcome.Accepted, unchanged.Outcome);
            Assert.Equal("scheduler-configuration-unchanged", unchanged.Reason.Code);
            Assert.Equal(updatedSnapshot.Version, unchanged.PreviousRegistryVersion);
            Assert.Equal(updatedSnapshot.Fingerprint, unchanged.PreviousRegistryFingerprint);
            Assert.Equal(updatedSnapshot.Version, unchanged.NewRegistryVersion);
            Assert.Equal(updatedSnapshot.Fingerprint, unchanged.NewRegistryFingerprint);
            Assert.Empty(unchanged.Operations);
            Assert.Empty(unchanged.Errors);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Reconcile_emits_lifecycle_materialized_events_for_created_and_updated_schedules()
    {
        var initialSnapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "daily-report",
                "0 55 17 ? * MON-FRI",
                "Run daily report")));
        var updatedSnapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "daily-report",
                "0 0 10 ? * MON-FRI",
                "Run updated report")));
        var lifecycleAudit = new RecordingSchedulerLifecycleAuditSink();
        var system = ActorSystem.Create("scheduler-coordinator-lifecycle-materialized");
        try
        {
            var coordinator = system.ActorOf(
                SchedulerCoordinator.Props(
                    NoopSchedulerQuartzAdapter.Instance,
                    new ManualTimeProvider(ImportAt),
                    NoopSchedulerPulseDeliveryStore.Instance,
                    NoopSchedulerPulseDispatcher.Instance,
                    AllowingSchedulerProactiveBudgetPolicy.Instance,
                    NoopSchedulerReconciliationAuditSink.Instance,
                    lifecycleAudit),
                "scheduler-coordinator-lifecycle-materialized");

            var first = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(initialSnapshot),
                AskTimeout);
            var repeated = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(initialSnapshot),
                AskTimeout);
            var updated = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(updatedSnapshot),
                AskTimeout);

            Assert.True(first.IsAccepted, string.Join(Environment.NewLine, first.Errors));
            Assert.True(repeated.IsAccepted, string.Join(Environment.NewLine, repeated.Errors));
            Assert.True(updated.IsAccepted, string.Join(Environment.NewLine, updated.Errors));

            Assert.Equal(
                new[]
                {
                    SchedulerLifecycleAuditStage.Materialized,
                    SchedulerLifecycleAuditStage.Materialized,
                },
                lifecycleAudit.Records.Select(record => record.Stage));

            var created = lifecycleAudit.Records[0];
            Assert.Equal(SchedulerLifecycleAuditOutcome.Accepted, created.Outcome);
            Assert.Equal("acme-delivery", created.OrganizationId?.Value);
            Assert.Equal("delivery-lead", created.PositionId?.Value);
            Assert.Equal("daily-report", created.ScheduleId?.Value);
            Assert.Equal(initialSnapshot.Version, created.RegistryVersion);
            Assert.Equal(initialSnapshot.Fingerprint, created.RegistryFingerprint);
            Assert.Equal(
                "job--acme-delivery--delivery-lead--daily-report",
                created.QuartzIdentity?.JobName);
            Assert.Null(created.Window);
            Assert.Null(created.IdempotencyKey);
            Assert.Null(created.MessageId);
            Assert.Null(created.ThreadId);
            Assert.Null(created.Source);

            var changed = lifecycleAudit.Records[1];
            Assert.Equal(updatedSnapshot.Fingerprint, changed.RegistryFingerprint);
            Assert.Equal(created.QuartzIdentity, changed.QuartzIdentity);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Reconcile_audits_rejected_configuration_without_applying_quartz_or_delivery_mutations()
    {
        var validSnapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "daily-report",
                "0 55 17 ? * MON-FRI",
                "Run daily report")));
        var invalidSnapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "broken-report",
                "not-a-cron",
                "Invalid schedule")));
        var quartz = new RecordingSchedulerQuartzAdapter();
        var deliveryStore = new RecordingSchedulerPulseDeliveryStore();
        var audit = new RecordingSchedulerReconciliationAuditSink();
        var system = ActorSystem.Create("scheduler-coordinator-reconcile-rejected-audit");
        try
        {
            var coordinator = system.ActorOf(
                SchedulerCoordinator.Props(
                    quartz,
                    new ManualTimeProvider(ImportAt),
                    deliveryStore,
                    NoopSchedulerPulseDispatcher.Instance,
                    AllowingSchedulerProactiveBudgetPolicy.Instance,
                    audit),
                "scheduler-coordinator-reconcile-rejected-audit");

            var accepted = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(validSnapshot),
                AskTimeout);
            var scheduleCallsAfterValidSnapshot = quartz.ScheduleCalls.Count;
            var unscheduleCallsAfterValidSnapshot = quartz.UnscheduleCalls.Count;
            var firedAfterValidSnapshot = deliveryStore.Fired.Count;
            var skippedAfterValidSnapshot = deliveryStore.Skipped.Count;

            var rejected = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(invalidSnapshot),
                AskTimeout);

            Assert.True(accepted.IsAccepted, string.Join(Environment.NewLine, accepted.Errors));
            Assert.False(rejected.IsAccepted);
            Assert.Equal(scheduleCallsAfterValidSnapshot, quartz.ScheduleCalls.Count);
            Assert.Equal(unscheduleCallsAfterValidSnapshot, quartz.UnscheduleCalls.Count);
            Assert.Equal(firedAfterValidSnapshot, deliveryStore.Fired.Count);
            Assert.Equal(skippedAfterValidSnapshot, deliveryStore.Skipped.Count);

            var rejectedAudit = Assert.Single(
                audit.Records,
                record => record.Outcome == SchedulerReconciliationAuditOutcome.Rejected);
            Assert.Equal("scheduler-configuration-rejected", rejectedAudit.Reason.Code);
            Assert.Equal(validSnapshot.Version, rejectedAudit.PreviousRegistryVersion);
            Assert.Equal(validSnapshot.Fingerprint, rejectedAudit.PreviousRegistryFingerprint);
            Assert.Equal(invalidSnapshot.Version, rejectedAudit.NewRegistryVersion);
            Assert.Equal(invalidSnapshot.Fingerprint, rejectedAudit.NewRegistryFingerprint);
            Assert.Empty(rejectedAudit.Operations);
            Assert.Contains(rejectedAudit.Errors, error => error.Code == "schedule-cron-invalid");
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Reconcile_pause_and_remove_preserve_existing_delivery_records()
    {
        var initialSnapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "pause-report",
                "0 55 17 ? * MON-FRI",
                "Run paused report"),
            new ScheduleEntryConfiguration(
                "remove-report",
                "0 55 17 ? * MON-FRI",
                "Run removed report")));
        var updatedSnapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "pause-report",
                "0 55 17 ? * MON-FRI",
                "Run paused report",
                isActive: false)));
        var deliveryStore = new RecordingSchedulerPulseDeliveryStore();
        var pulseDispatcher = new RecordingSchedulerPulseDispatcher();
        var audit = new RecordingSchedulerReconciliationAuditSink();
        var system = ActorSystem.Create("scheduler-coordinator-reconcile-preserve-deliveries");
        try
        {
            var coordinator = system.ActorOf(
                SchedulerCoordinator.Props(
                    NoopSchedulerQuartzAdapter.Instance,
                    new ManualTimeProvider(ImportAt),
                    deliveryStore,
                    pulseDispatcher,
                    AllowingSchedulerProactiveBudgetPolicy.Instance,
                    audit),
                "scheduler-coordinator-reconcile-preserve-deliveries");
            var reconciliation = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(initialSnapshot),
                AskTimeout);
            Assert.True(reconciliation.IsAccepted, string.Join(Environment.NewLine, reconciliation.Errors));

            var firedAtUtc = new DateTimeOffset(2026, 7, 3, 17, 10, 0, TimeSpan.Zero);
            var pauseDispatch = await coordinator.Ask<SchedulerDispatchResult>(
                new DispatchSchedulerSchedule(
                    reconciliation.Materializations.Single(materialization =>
                        materialization.Key.Schedule == ScheduleId.From("pause-report")).Key,
                    firedAtUtc),
                AskTimeout);
            var removeDispatch = await coordinator.Ask<SchedulerDispatchResult>(
                new DispatchSchedulerSchedule(
                    reconciliation.Materializations.Single(materialization =>
                        materialization.Key.Schedule == ScheduleId.From("remove-report")).Key,
                    firedAtUtc),
                AskTimeout);
            Assert.True(pauseDispatch.IsAccepted, pauseDispatch.Error?.ToString());
            Assert.True(removeDispatch.IsAccepted, removeDispatch.Error?.ToString());

            var firedBeforeReconfiguration = deliveryStore.Fired.Count;
            var deliveredBeforeReconfiguration = deliveryStore.Delivered.Count;
            var skippedBeforeReconfiguration = deliveryStore.Skipped.Count;
            var failedBeforeReconfiguration = deliveryStore.Failed.Count;

            var reconfigured = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(updatedSnapshot),
                AskTimeout);

            Assert.True(reconfigured.IsAccepted, string.Join(Environment.NewLine, reconfigured.Errors));
            Assert.Equal(firedBeforeReconfiguration, deliveryStore.Fired.Count);
            Assert.Equal(deliveredBeforeReconfiguration, deliveryStore.Delivered.Count);
            Assert.Equal(skippedBeforeReconfiguration, deliveryStore.Skipped.Count);
            Assert.Equal(failedBeforeReconfiguration, deliveryStore.Failed.Count);

            var paused = await deliveryStore.FindAsync(pauseDispatch.Dispatch!.IdempotencyKey);
            var removed = await deliveryStore.FindAsync(removeDispatch.Dispatch!.IdempotencyKey);
            Assert.Equal(SchedulerPulseDeliveryStatus.Delivered, paused?.Status);
            Assert.Equal(SchedulerPulseDeliveryStatus.Delivered, removed?.Status);

            var changedAudit = audit.Records.Last();
            Assert.Equal("scheduler-configuration-changed", changedAudit.Reason.Code);
            Assert.Contains(changedAudit.Operations, operation =>
                operation.Kind == SchedulerScheduleReconciliationOperationKind.Pause
                && operation.Key.Schedule == ScheduleId.From("pause-report"));
            Assert.Contains(changedAudit.Operations, operation =>
                operation.Kind == SchedulerScheduleReconciliationOperationKind.Remove
                && operation.Key.Schedule == ScheduleId.From("remove-report"));
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Reconcile_invalid_snapshot_does_not_schedule_new_quartz_jobs()
    {
        var validSnapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "daily-report",
                "0 55 17 ? * MON-FRI",
                "Run daily report")));
        var invalidSnapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "bad-report",
                "99 99 99 * * MON-FRI",
                "This schedule is invalid")));
        var quartz = new RecordingSchedulerQuartzAdapter();
        var system = ActorSystem.Create("scheduler-coordinator-quartz-invalid");
        try
        {
            var coordinator = system.ActorOf(
                SchedulerCoordinator.Props(quartz, TimeProvider.System),
                "scheduler-coordinator-quartz-invalid");

            var accepted = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(validSnapshot),
                AskTimeout);
            Assert.True(accepted.IsAccepted, string.Join(Environment.NewLine, accepted.Errors));
            Assert.Single(quartz.Jobs);

            var rejected = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(invalidSnapshot),
                AskTimeout);

            Assert.False(rejected.IsAccepted);
            Assert.Single(quartz.Jobs);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Reconcile_rejects_invalid_snapshots_without_replacing_current_materialization()
    {
        var validSnapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "daily-report",
                "0 55 17 ? * MON-FRI",
                "Run daily report")));
        var invalidSnapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "bad-report",
                "99 99 99 * * MON-FRI",
                "This schedule is invalid")));
        var system = ActorSystem.Create("scheduler-coordinator-invalid");
        try
        {
            var coordinator = system.ActorOf(
                ProtocolOnlyCoordinatorProps(),
                "scheduler-coordinator-invalid");

            var accepted = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(validSnapshot),
                AskTimeout);
            Assert.True(accepted.IsAccepted, string.Join(Environment.NewLine, accepted.Errors));

            var rejected = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(invalidSnapshot),
                AskTimeout);

            Assert.False(rejected.IsAccepted);
            Assert.Contains(rejected.Errors, error => error.Code == "schedule-cron-invalid");

            var state = await coordinator.Ask<SchedulerCoordinatorState>(
                GetSchedulerCoordinatorState.Instance,
                AskTimeout);

            Assert.Equal(
                accepted.Materializations.Select(materialization => materialization.Key.Value),
                state.Materializations.Select(materialization => materialization.Key.Value));
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Reconcile_skips_latest_missed_window_for_non_critical_schedule_without_delivery()
    {
        var snapshot = await ImportedSnapshotAsync(
            WithDeliveryLeadSchedules(
                ExampleConfiguration(),
                new ScheduleEntryConfiguration(
                    "daily-report",
                    "0 55 17 ? * MON-FRI",
                    "Run daily report")),
            new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero));
        var nowUtc = new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);
        var deliveryStore = new RecordingSchedulerPulseDeliveryStore();
        var pulseDispatcher = new RecordingSchedulerPulseDispatcher();
        var system = ActorSystem.Create("scheduler-coordinator-missed-skip");
        try
        {
            var coordinator = system.ActorOf(
                SchedulerCoordinator.Props(
                    NoopSchedulerQuartzAdapter.Instance,
                    new ManualTimeProvider(nowUtc),
                    deliveryStore,
                    pulseDispatcher),
                "scheduler-coordinator-missed-skip");

            var result = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(snapshot),
                AskTimeout);

            Assert.True(result.IsAccepted, string.Join(Environment.NewLine, result.Errors));
            Assert.Empty(deliveryStore.Fired);
            var skipped = Assert.Single(deliveryStore.Skipped);
            Assert.Equal("scheduler-missed-window-skipped", skipped.Reason?.Code);
            Assert.Equal(
                "acme-delivery/delivery-lead/daily-report/2026-07-03T16:55:00.0000000Z/2026-07-06T16:55:00.0000000Z",
                skipped.IdempotencyKey.Value);
            Assert.Equal(nowUtc, skipped.OccurredAtUtc);
            Assert.Empty(pulseDispatcher.Pulses);

            var state = await coordinator.Ask<SchedulerCoordinatorState>(
                GetSchedulerCoordinatorState.Instance,
                AskTimeout);
            Assert.Empty(state.PendingDispatches);

            var repeated = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(snapshot),
                AskTimeout);
            Assert.True(repeated.IsAccepted, string.Join(Environment.NewLine, repeated.Errors));
            Assert.Empty(deliveryStore.Fired);
            Assert.Single(deliveryStore.Skipped);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Reconcile_catches_up_latest_missed_critical_window_once_when_budget_is_available()
    {
        var snapshot = await ImportedSnapshotAsync(
            WithDeliveryLeadSchedules(
                ExampleConfiguration(),
                new ScheduleEntryConfiguration(
                    "critical-report",
                    "0 55 17 ? * MON-FRI",
                    "Run critical report",
                    isCritical: true,
                    catchUp: "catch-up-once")),
            new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero));
        var nowUtc = new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);
        var deliveryStore = new RecordingSchedulerPulseDeliveryStore();
        var pulseDispatcher = new RecordingSchedulerPulseDispatcher();
        var system = ActorSystem.Create("scheduler-coordinator-missed-catch-up");
        try
        {
            var coordinator = system.ActorOf(
                SchedulerCoordinator.Props(
                    NoopSchedulerQuartzAdapter.Instance,
                    new ManualTimeProvider(nowUtc),
                    deliveryStore,
                    pulseDispatcher),
                "scheduler-coordinator-missed-catch-up");

            var result = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(snapshot),
                AskTimeout);

            Assert.True(result.IsAccepted, string.Join(Environment.NewLine, result.Errors));
            var fired = Assert.Single(deliveryStore.Fired);
            Assert.Equal(
                "acme-delivery/delivery-lead/critical-report/2026-07-03T16:55:00.0000000Z/2026-07-06T16:55:00.0000000Z",
                fired.IdempotencyKey.Value);
            Assert.Equal(nowUtc, fired.OccurredAtUtc);
            Assert.Empty(deliveryStore.Skipped);
            var delivered = Assert.Single(deliveryStore.Delivered);
            Assert.Equal(fired.IdempotencyKey, delivered.IdempotencyKey);

            var pulse = Assert.Single(pulseDispatcher.Pulses);
            Assert.Equal("critical-report", pulse.ScheduleId);
            Assert.Equal("Run critical report", pulse.Payload);
            Assert.Equal(nowUtc, pulse.SentAt);
            Assert.Equal(fired.MessageId, pulse.Id);
            Assert.Equal(fired.ThreadId, pulse.Thread);

            var state = await coordinator.Ask<SchedulerCoordinatorState>(
                GetSchedulerCoordinatorState.Instance,
                AskTimeout);
            var pending = Assert.Single(state.PendingDispatches);
            Assert.Equal(fired.IdempotencyKey, pending.IdempotencyKey);

            var repeated = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(snapshot),
                AskTimeout);
            Assert.True(repeated.IsAccepted, string.Join(Environment.NewLine, repeated.Errors));
            Assert.Single(deliveryStore.Fired);
            Assert.Single(deliveryStore.Delivered);
            Assert.Single(pulseDispatcher.Pulses);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Reconcile_skips_critical_catch_up_when_proactive_budget_is_unavailable()
    {
        var snapshot = await ImportedSnapshotAsync(
            WithDeliveryLeadSchedules(
                ExampleConfiguration(),
                new ScheduleEntryConfiguration(
                    "critical-report",
                    "0 55 17 ? * MON-FRI",
                    "Run critical report",
                    isCritical: true,
                    catchUp: "catch-up-once")),
            new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero));
        var nowUtc = new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);
        var deliveryStore = new RecordingSchedulerPulseDeliveryStore();
        var pulseDispatcher = new RecordingSchedulerPulseDispatcher();
        var budgetPolicy = new RecordingSchedulerProactiveBudgetPolicy(hasAvailableBudget: false);
        var system = ActorSystem.Create("scheduler-coordinator-missed-catch-up-budget");
        try
        {
            var coordinator = system.ActorOf(
                SchedulerCoordinator.Props(
                    NoopSchedulerQuartzAdapter.Instance,
                    new ManualTimeProvider(nowUtc),
                    deliveryStore,
                    pulseDispatcher,
                    budgetPolicy),
                "scheduler-coordinator-missed-catch-up-budget");

            var result = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(snapshot),
                AskTimeout);

            Assert.True(result.IsAccepted, string.Join(Environment.NewLine, result.Errors));
            Assert.Single(deliveryStore.Fired);
            var skipped = Assert.Single(deliveryStore.Skipped);
            Assert.Equal("scheduler-proactive-budget-unavailable", skipped.Reason?.Code);
            Assert.Single(budgetPolicy.Requests);
            Assert.Empty(deliveryStore.Delivered);
            Assert.Empty(pulseDispatcher.Pulses);

            var state = await coordinator.Ask<SchedulerCoordinatorState>(
                GetSchedulerCoordinatorState.Instance,
                AskTimeout);
            Assert.Empty(state.PendingDispatches);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Dispatch_rejects_unknown_schedules_and_records_known_dispatches()
    {
        var snapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "daily-report",
                "0 55 17 ? * MON-FRI",
                "Run daily report")));
        var firedAtUtc = new DateTimeOffset(2026, 7, 3, 17, 10, 0, TimeSpan.Zero);
        var system = ActorSystem.Create("scheduler-coordinator-dispatch");
        try
        {
            var coordinator = system.ActorOf(
                ProtocolOnlyCoordinatorProps(),
                "scheduler-coordinator-dispatch");
            var reconciliation = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(snapshot),
                AskTimeout);
            Assert.True(reconciliation.IsAccepted, string.Join(Environment.NewLine, reconciliation.Errors));

            var unknown = await coordinator.Ask<SchedulerDispatchResult>(
                new DispatchSchedulerSchedule(
                    SchedulerScheduleKey.From(
                        OrganizationId.From("acme-delivery"),
                        PositionId.From("delivery-lead"),
                        ScheduleId.From("missing-report")),
                    firedAtUtc),
                AskTimeout);

            Assert.False(unknown.IsAccepted);
            Assert.Equal("schedule-not-materialized", unknown.Error?.Code);

            var knownKey = Assert.Single(reconciliation.Materializations).Key;
            var accepted = await coordinator.Ask<SchedulerDispatchResult>(
                new DispatchSchedulerSchedule(knownKey, firedAtUtc),
                AskTimeout);

            Assert.True(accepted.IsAccepted, accepted.Error?.ToString());
            Assert.NotNull(accepted.Dispatch);
            Assert.Equal(knownKey, accepted.Dispatch.Key);
            Assert.Equal(firedAtUtc, accepted.Dispatch.FiredAtUtc);
            Assert.Equal(new DateTimeOffset(2026, 7, 3, 16, 55, 0, TimeSpan.Zero), accepted.Dispatch.Window.Start);
            Assert.Equal(new DateTimeOffset(2026, 7, 6, 16, 55, 0, TimeSpan.Zero), accepted.Dispatch.Window.End);
            Assert.Equal(
                "acme-delivery/delivery-lead/daily-report/2026-07-03T16:55:00.0000000Z/2026-07-06T16:55:00.0000000Z",
                accepted.Dispatch.IdempotencyKey.Value);
            var pulse = DispatchPulse(accepted.Dispatch);
            Assert.Equal(knownKey.Organization, pulse.OrganizationId);
            Assert.Equal(knownKey.Schedule.Value, pulse.ScheduleId);
            Assert.Equal("Run daily report", pulse.Payload);
            Assert.Equal(firedAtUtc, pulse.SentAt);

            var state = await coordinator.Ask<SchedulerCoordinatorState>(
                GetSchedulerCoordinatorState.Instance,
                AskTimeout);
            var pending = Assert.Single(state.PendingDispatches);
            Assert.Equal(knownKey, pending.Key);
            Assert.Equal(firedAtUtc, pending.FiredAtUtc);
            Assert.Equal(accepted.Dispatch.Window, pending.Window);
            Assert.Equal(accepted.Dispatch.IdempotencyKey, pending.IdempotencyKey);
            Assert.Equal(pulse, DispatchPulse(pending));
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Quartz_fire_for_materialized_schedule_records_dispatch_with_clock_timestamp()
    {
        var snapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "daily-report",
                "0 55 17 ? * MON-FRI",
                "Run daily report")));
        var firedAtUtc = new DateTimeOffset(2026, 7, 3, 17, 10, 0, TimeSpan.Zero);
        var system = ActorSystem.Create("scheduler-coordinator-quartz-fire");
        try
        {
            var coordinator = system.ActorOf(
                SchedulerCoordinator.Props(
                    new RecordingSchedulerQuartzAdapter(),
                    new ManualTimeProvider(firedAtUtc)),
                "scheduler-coordinator-quartz-fire");
            var reconciliation = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(snapshot),
                AskTimeout);
            Assert.True(reconciliation.IsAccepted, string.Join(Environment.NewLine, reconciliation.Errors));

            var knownKey = Assert.Single(reconciliation.Materializations).Key;
            var accepted = await coordinator.Ask<SchedulerDispatchResult>(
                new SchedulerQuartzScheduleFired(knownKey),
                AskTimeout);

            Assert.True(accepted.IsAccepted, accepted.Error?.ToString());
            Assert.NotNull(accepted.Dispatch);
            Assert.Equal(knownKey, accepted.Dispatch.Key);
            Assert.Equal(firedAtUtc, accepted.Dispatch.FiredAtUtc);
            Assert.Equal(new DateTimeOffset(2026, 7, 3, 16, 55, 0, TimeSpan.Zero), accepted.Dispatch.Window.Start);
            Assert.Equal(new DateTimeOffset(2026, 7, 6, 16, 55, 0, TimeSpan.Zero), accepted.Dispatch.Window.End);
            var pulse = DispatchPulse(accepted.Dispatch);
            Assert.Equal(knownKey.Organization, pulse.OrganizationId);
            Assert.Equal(knownKey.Schedule.Value, pulse.ScheduleId);
            Assert.Equal(firedAtUtc, pulse.SentAt);

            var state = await coordinator.Ask<SchedulerCoordinatorState>(
                GetSchedulerCoordinatorState.Instance,
                AskTimeout);
            var pending = Assert.Single(state.PendingDispatches);
            Assert.Equal(knownKey, pending.Key);
            Assert.Equal(firedAtUtc, pending.FiredAtUtc);
            Assert.Equal(accepted.Dispatch.IdempotencyKey, pending.IdempotencyKey);
            Assert.Equal(pulse, DispatchPulse(pending));
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Quartz_fire_for_unknown_schedule_is_rejected_without_pending_dispatch()
    {
        var firedAtUtc = new DateTimeOffset(2026, 7, 4, 17, 55, 1, TimeSpan.Zero);
        var system = ActorSystem.Create("scheduler-coordinator-quartz-unknown-fire");
        try
        {
            var coordinator = system.ActorOf(
                SchedulerCoordinator.Props(
                    new RecordingSchedulerQuartzAdapter(),
                    new ManualTimeProvider(firedAtUtc)),
                "scheduler-coordinator-quartz-unknown-fire");

            var rejected = await coordinator.Ask<SchedulerDispatchResult>(
                new SchedulerQuartzScheduleFired(SchedulerScheduleKey.From(
                    OrganizationId.From("acme-delivery"),
                    PositionId.From("delivery-lead"),
                    ScheduleId.From("missing-report"))),
                AskTimeout);

            Assert.False(rejected.IsAccepted);
            Assert.Equal("schedule-not-materialized", rejected.Error?.Code);

            var state = await coordinator.Ask<SchedulerCoordinatorState>(
                GetSchedulerCoordinatorState.Instance,
                AskTimeout);
            Assert.Empty(state.PendingDispatches);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Default_props_create_quartz_child_when_reconciliation_schedules_jobs()
    {
        var snapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "daily-report",
                "0/30 * * * * ?",
                "Run daily report")));
        var system = ActorSystem.Create("scheduler-coordinator-default-quartz");
        try
        {
            var coordinator = system.ActorOf(
                SchedulerCoordinator.Props(),
                "scheduler-coordinator-default-quartz");

            var reconciliation = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(snapshot),
                AskTimeout);
            Assert.True(reconciliation.IsAccepted, string.Join(Environment.NewLine, reconciliation.Errors));

            var identity = await system
                .ActorSelection($"{coordinator.Path}/scheduler-coordinator-quartz")
                .Ask<ActorIdentity>(new Identify("quartz"), AskTimeout);

            Assert.NotNull(identity.Subject);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public void Quartz_adapter_builds_trigger_with_deterministic_keys()
    {
        var key = SchedulerScheduleKey.From(
            OrganizationId.From("acme-delivery"),
            PositionId.From("delivery-lead"),
            ScheduleId.From("daily-report"));
        var job = new SchedulerQuartzJob(
            key,
            DomainCronExpression.From("0 55 17 ? * MON-FRI"),
            "Europe/Lisbon");

        var trigger = AkkaQuartzSchedulerAdapter.BuildTrigger(job);

        Assert.Equal(
            new TriggerKey("trigger--acme-delivery--delivery-lead--daily-report", "hive-scheduler-triggers"),
            trigger.Key);
        Assert.Equal(
            new JobKey("job--acme-delivery--delivery-lead--daily-report", "hive-scheduler-jobs"),
            trigger.JobKey);
        var cronTrigger = Assert.IsAssignableFrom<ICronTrigger>(trigger);
        Assert.Equal("0 55 17 ? * MON-FRI", cronTrigger.CronExpressionString);
        Assert.Equal(TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon"), cronTrigger.TimeZone);
    }

    [Fact]
    public async Task Idempotent_quartz_actor_replaces_existing_job_with_same_keys()
    {
        var key = SchedulerScheduleKey.From(
            OrganizationId.From("acme-delivery"),
            PositionId.From("delivery-lead"),
            ScheduleId.From("daily-report"));
        var initialJob = new SchedulerQuartzJob(
            key,
            DomainCronExpression.From("0 55 17 ? * MON-FRI"),
            "Europe/Lisbon");
        var updatedJob = new SchedulerQuartzJob(
            key,
            DomainCronExpression.From("0 0 18 ? * MON-FRI"),
            "Europe/Lisbon");
        var scheduler = await NewInMemoryQuartzSchedulerAsync();
        var system = ActorSystem.Create("scheduler-coordinator-idempotent-quartz-actor");
        try
        {
            var quartzActor = system.ActorOf(
                Props.Create(() => new HiveQuartzActor(scheduler)),
                "idempotent-quartz");

            var first = await quartzActor.Ask<JobCreated>(
                new CreateJob(
                    ActorRefs.Nobody,
                    new SchedulerQuartzScheduleFired(initialJob.Key),
                    AkkaQuartzSchedulerAdapter.BuildTrigger(initialJob)),
                AskTimeout);
            var second = await quartzActor.Ask<JobCreated>(
                new CreateJob(
                    ActorRefs.Nobody,
                    new SchedulerQuartzScheduleFired(updatedJob.Key),
                    AkkaQuartzSchedulerAdapter.BuildTrigger(updatedJob)),
                AskTimeout);

            Assert.Equal(initialJob.Identity.JobName, first.JobKey.Name);
            Assert.Equal(updatedJob.Identity.JobName, second.JobKey.Name);
            var jobKeys = await scheduler.GetJobKeys(
                GroupMatcher<JobKey>.GroupEquals(SchedulerQuartzIdentity.JobGroupName));
            var jobKey = Assert.Single(jobKeys);
            var triggers = await scheduler.GetTriggersOfJob(jobKey);
            var trigger = Assert.IsAssignableFrom<ICronTrigger>(Assert.Single(triggers));
            Assert.Equal("0 0 18 ? * MON-FRI", trigger.CronExpressionString);
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false);
            await system.Terminate();
        }
    }

    private static OrganizationConfiguration WithDeliveryLeadSchedules(
        OrganizationConfiguration configuration,
        params ScheduleEntryConfiguration[] schedules) =>
        new(
            configuration.Organization,
            configuration.Units,
            configuration.Positions
                .Select(position => position.Id.Value == "delivery-lead"
                    ? new PositionConfiguration(
                        position.Id,
                        position.Unit,
                        new OccupantConfiguration(
                            position.Occupant.Type,
                            position.Occupant.IdentityPromptRef,
                            position.Occupant.Ai,
                            new WorkingHoursConfiguration("09:00", "18:00"),
                            position.Occupant.Authority,
                            schedules,
                            position.Occupant.Subscriptions,
                            position.Occupant.Tools),
                        position.ReportsTo,
                        position.Name,
                        "Europe/Lisbon")
                    : position)
                .ToArray(),
            configuration.Prompts);

    private static async Task<OrganizationRegistrySnapshot> ImportedSnapshotAsync(
        OrganizationConfiguration configuration,
        DateTimeOffset? importAtUtc = null)
    {
        var registry = new InMemoryOrganizationRegistry();
        var imported = await new OrganizationConfigurationImporter(
            registry,
            new ManualTimeProvider(importAtUtc ?? ImportAt))
            .ImportAsync(configuration);

        Assert.Equal(OrganizationImportStatus.Applied, imported.Status);
        return imported.Snapshot!;
    }

    private static OrganizationConfiguration ExampleConfiguration()
    {
        var result = new OrganizationConfigurationParser().ParseFile(
            Path.Combine(RepositoryRoot, "config", "organizations", "acme-delivery", "organization.yaml"));

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Errors));
        return result.Configuration!;
    }

    private static string RepositoryRoot
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Hive.sln")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new InvalidOperationException("Could not locate the Hive repository root.");
        }
    }

    private static async Task<Quartz.IScheduler> NewInMemoryQuartzSchedulerAsync()
    {
        var properties = new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = Guid.NewGuid().ToString("N"),
            ["quartz.threadPool.threadCount"] = "1",
        };
        var scheduler = await new StdSchedulerFactory(properties).GetScheduler();
        await scheduler.Start();
        return scheduler;
    }

    private static Props ProtocolOnlyCoordinatorProps() =>
        SchedulerCoordinator.Props(NoopSchedulerQuartzAdapter.Instance, TimeProvider.System);

    private static SchedulerScheduleMaterialization Materialization(
        string scheduleId,
        string cron,
        string timeZone,
        bool isCritical = false,
        LoadedScheduleWorkingHours? workingHours = null,
        CatchUpPolicy catchUp = CatchUpPolicy.Skip,
        DateTimeOffset? declaredAtUtc = null)
    {
        var key = SchedulerScheduleKey.From(
            OrganizationId.From("acme-delivery"),
            PositionId.From("delivery-lead"),
            ScheduleId.From(scheduleId));
        var definition = ScheduleDefinition.Create(
            key.Schedule,
            DomainCronExpression.From(cron),
            timeZone,
            "Run scheduled work",
            Priority.Normal,
            isCritical,
            catchUp);

        return new SchedulerScheduleMaterialization(
            key,
            definition,
            workingHours ?? new LoadedScheduleWorkingHours(new TimeOnly(9, 0), new TimeOnly(18, 0)),
            declaredAtUtc);
    }

    [Fact]
    public async Task Dispatch_persists_delivers_and_marks_accepted_delivery_state()
    {
        var snapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "daily-report",
                "0 55 17 ? * MON-FRI",
                "Run daily report")));
        var firedAtUtc = new DateTimeOffset(2026, 7, 3, 17, 10, 0, TimeSpan.Zero);
        var deliveryStore = new RecordingSchedulerPulseDeliveryStore();
        var pulseDispatcher = new RecordingSchedulerPulseDispatcher();
        var system = ActorSystem.Create("scheduler-coordinator-delivery");
        try
        {
            var coordinator = system.ActorOf(
                SchedulerCoordinator.Props(
                    NoopSchedulerQuartzAdapter.Instance,
                    TimeProvider.System,
                    deliveryStore,
                    pulseDispatcher),
                "scheduler-coordinator-delivery");
            var reconciliation = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(snapshot),
                AskTimeout);
            Assert.True(reconciliation.IsAccepted, string.Join(Environment.NewLine, reconciliation.Errors));

            var unknown = await coordinator.Ask<SchedulerDispatchResult>(
                new DispatchSchedulerSchedule(
                    SchedulerScheduleKey.From(
                        OrganizationId.From("acme-delivery"),
                        PositionId.From("delivery-lead"),
                        ScheduleId.From("missing-report")),
                    firedAtUtc),
                AskTimeout);

            Assert.False(unknown.IsAccepted);
            Assert.Empty(deliveryStore.Fired);
            Assert.Empty(pulseDispatcher.Pulses);

            var knownKey = Assert.Single(reconciliation.Materializations).Key;
            var accepted = await coordinator.Ask<SchedulerDispatchResult>(
                new DispatchSchedulerSchedule(knownKey, firedAtUtc),
                AskTimeout);

            Assert.True(accepted.IsAccepted, accepted.Error?.ToString());
            var persisted = Assert.Single(deliveryStore.Fired);
            Assert.Equal(accepted.Dispatch!.IdempotencyKey, persisted.IdempotencyKey);
            Assert.Equal(accepted.Dispatch.Pulse.Id, persisted.MessageId);
            Assert.Equal(accepted.Dispatch.Pulse.Thread, persisted.ThreadId);
            Assert.Equal(firedAtUtc, persisted.OccurredAtUtc);
            Assert.Equal(accepted.Dispatch.Pulse, Assert.Single(pulseDispatcher.Pulses));
            var delivered = Assert.Single(deliveryStore.Delivered);
            Assert.Equal(accepted.Dispatch.IdempotencyKey, delivered.IdempotencyKey);
            Assert.Equal(firedAtUtc, delivered.OccurredAtUtc);

            var state = await coordinator.Ask<SchedulerCoordinatorState>(
                GetSchedulerCoordinatorState.Instance,
                AskTimeout);
            Assert.Single(state.PendingDispatches);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Dispatch_emits_lifecycle_fired_and_delivered_events_with_window_and_message_ids()
    {
        var snapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "daily-report",
                "0 55 17 ? * MON-FRI",
                "Run daily report")));
        var firedAtUtc = new DateTimeOffset(2026, 7, 3, 17, 10, 0, TimeSpan.Zero);
        var deliveryStore = new RecordingSchedulerPulseDeliveryStore();
        var pulseDispatcher = new RecordingSchedulerPulseDispatcher();
        var lifecycleAudit = new RecordingSchedulerLifecycleAuditSink();
        var system = ActorSystem.Create("scheduler-coordinator-lifecycle-delivered");
        try
        {
            var coordinator = system.ActorOf(
                SchedulerCoordinator.Props(
                    NoopSchedulerQuartzAdapter.Instance,
                    TimeProvider.System,
                    deliveryStore,
                    pulseDispatcher,
                    AllowingSchedulerProactiveBudgetPolicy.Instance,
                    NoopSchedulerReconciliationAuditSink.Instance,
                    lifecycleAudit),
                "scheduler-coordinator-lifecycle-delivered");
            var reconciliation = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(snapshot),
                AskTimeout);
            Assert.True(reconciliation.IsAccepted, string.Join(Environment.NewLine, reconciliation.Errors));

            var countAfterReconcile = lifecycleAudit.Records.Count;
            var unknown = await coordinator.Ask<SchedulerDispatchResult>(
                new DispatchSchedulerSchedule(
                    SchedulerScheduleKey.From(
                        OrganizationId.From("acme-delivery"),
                        PositionId.From("delivery-lead"),
                        ScheduleId.From("missing-report")),
                    firedAtUtc),
                AskTimeout);
            Assert.False(unknown.IsAccepted);
            Assert.Equal(countAfterReconcile, lifecycleAudit.Records.Count);

            var knownKey = Assert.Single(reconciliation.Materializations).Key;
            var accepted = await coordinator.Ask<SchedulerDispatchResult>(
                new DispatchSchedulerSchedule(knownKey, firedAtUtc),
                AskTimeout);

            Assert.True(accepted.IsAccepted, accepted.Error?.ToString());
            Assert.Equal(
                new[]
                {
                    SchedulerLifecycleAuditStage.Materialized,
                    SchedulerLifecycleAuditStage.Fired,
                    SchedulerLifecycleAuditStage.Delivered,
                },
                lifecycleAudit.Records.Select(record => record.Stage));

            var fired = lifecycleAudit.Records[1];
            var delivered = lifecycleAudit.Records[2];
            Assert.Equal(SchedulerLifecycleAuditSource.Direct, fired.Source);
            Assert.Equal(SchedulerLifecycleAuditOutcome.Accepted, fired.Outcome);
            Assert.Equal(accepted.Dispatch!.Window, fired.Window);
            Assert.Equal(accepted.Dispatch.IdempotencyKey, fired.IdempotencyKey);
            Assert.Equal(accepted.Dispatch.Pulse.Id, fired.MessageId);
            Assert.Equal(accepted.Dispatch.Pulse.Thread, fired.ThreadId);
            Assert.Null(fired.Reason);

            Assert.Equal(SchedulerLifecycleAuditSource.Direct, delivered.Source);
            Assert.Equal(SchedulerLifecycleAuditOutcome.Accepted, delivered.Outcome);
            Assert.Equal(fired.IdempotencyKey, delivered.IdempotencyKey);
            Assert.Equal(fired.MessageId, delivered.MessageId);
            Assert.Equal(fired.ThreadId, delivered.ThreadId);
            Assert.Null(delivered.Reason);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Dispatch_emits_lifecycle_redelivered_when_the_delivery_store_reports_existing_key()
    {
        var snapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "daily-report",
                "0 55 17 ? * MON-FRI",
                "Run daily report")));
        var firstFire = new DateTimeOffset(2026, 7, 3, 17, 10, 0, TimeSpan.Zero);
        var repeatedFire = new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);
        var deliveryStore = new RecordingSchedulerPulseDeliveryStore();
        var pulseDispatcher = new RecordingSchedulerPulseDispatcher();
        var lifecycleAudit = new RecordingSchedulerLifecycleAuditSink();
        var system = ActorSystem.Create("scheduler-coordinator-lifecycle-redelivery");
        try
        {
            var coordinator = system.ActorOf(
                SchedulerCoordinator.Props(
                    NoopSchedulerQuartzAdapter.Instance,
                    TimeProvider.System,
                    deliveryStore,
                    pulseDispatcher,
                    AllowingSchedulerProactiveBudgetPolicy.Instance,
                    NoopSchedulerReconciliationAuditSink.Instance,
                    lifecycleAudit),
                "scheduler-coordinator-lifecycle-redelivery");
            var reconciliation = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(snapshot),
                AskTimeout);
            Assert.True(reconciliation.IsAccepted, string.Join(Environment.NewLine, reconciliation.Errors));

            var knownKey = Assert.Single(reconciliation.Materializations).Key;
            var first = await coordinator.Ask<SchedulerDispatchResult>(
                new DispatchSchedulerSchedule(knownKey, firstFire),
                AskTimeout);
            var repeated = await coordinator.Ask<SchedulerDispatchResult>(
                new DispatchSchedulerSchedule(knownKey, repeatedFire),
                AskTimeout);

            Assert.True(first.IsAccepted, first.Error?.ToString());
            Assert.True(repeated.IsAccepted, repeated.Error?.ToString());
            Assert.Equal(first.Dispatch!.IdempotencyKey, repeated.Dispatch!.IdempotencyKey);
            Assert.Equal(first.Dispatch.Pulse.Id, repeated.Dispatch.Pulse.Id);
            Assert.Equal(first.Dispatch.Pulse.Thread, repeated.Dispatch.Pulse.Thread);
            Assert.Equal(
                new[]
                {
                    SchedulerLifecycleAuditStage.Materialized,
                    SchedulerLifecycleAuditStage.Fired,
                    SchedulerLifecycleAuditStage.Delivered,
                    SchedulerLifecycleAuditStage.Redelivered,
                    SchedulerLifecycleAuditStage.Delivered,
                },
                lifecycleAudit.Records.Select(record => record.Stage));
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Dispatch_emits_lifecycle_skipped_and_failed_events_with_structured_reasons()
    {
        var outsideHoursSnapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "end-of-day-report",
                "0 0 18 ? * MON-FRI",
                "Run end of day report")));
        var failingSnapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "daily-report",
                "0 55 17 ? * MON-FRI",
                "Run daily report")));
        var skippedAtUtc = new DateTimeOffset(2026, 7, 3, 17, 0, 0, TimeSpan.Zero);
        var firedAtUtc = new DateTimeOffset(2026, 7, 3, 17, 10, 0, TimeSpan.Zero);

        var skippedStore = new RecordingSchedulerPulseDeliveryStore();
        var skippedAudit = new RecordingSchedulerLifecycleAuditSink();
        var skippedSystem = ActorSystem.Create("scheduler-coordinator-lifecycle-skipped");
        try
        {
            var coordinator = skippedSystem.ActorOf(
                SchedulerCoordinator.Props(
                    NoopSchedulerQuartzAdapter.Instance,
                    TimeProvider.System,
                    skippedStore,
                    NoopSchedulerPulseDispatcher.Instance,
                    AllowingSchedulerProactiveBudgetPolicy.Instance,
                    NoopSchedulerReconciliationAuditSink.Instance,
                    skippedAudit),
                "scheduler-coordinator-lifecycle-skipped");
            var reconciliation = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(outsideHoursSnapshot),
                AskTimeout);
            Assert.True(reconciliation.IsAccepted, string.Join(Environment.NewLine, reconciliation.Errors));

            var skipped = await coordinator.Ask<SchedulerDispatchResult>(
                new DispatchSchedulerSchedule(Assert.Single(reconciliation.Materializations).Key, skippedAtUtc),
                AskTimeout);

            Assert.False(skipped.IsAccepted);
            Assert.Equal(SchedulerLifecycleAuditStage.Skipped, skippedAudit.Records.Last().Stage);
            Assert.Equal(SchedulerLifecycleAuditOutcome.Skipped, skippedAudit.Records.Last().Outcome);
            Assert.Equal("scheduler-outside-working-hours", skippedAudit.Records.Last().Reason?.Code);
        }
        finally
        {
            await skippedSystem.Terminate();
        }

        var budgetStore = new RecordingSchedulerPulseDeliveryStore();
        var budgetAudit = new RecordingSchedulerLifecycleAuditSink();
        var budgetSystem = ActorSystem.Create("scheduler-coordinator-lifecycle-budget-skipped");
        try
        {
            var coordinator = budgetSystem.ActorOf(
                SchedulerCoordinator.Props(
                    NoopSchedulerQuartzAdapter.Instance,
                    TimeProvider.System,
                    budgetStore,
                    NoopSchedulerPulseDispatcher.Instance,
                    new RecordingSchedulerProactiveBudgetPolicy(hasAvailableBudget: false),
                    NoopSchedulerReconciliationAuditSink.Instance,
                    budgetAudit),
                "scheduler-coordinator-lifecycle-budget-skipped");
            var reconciliation = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(failingSnapshot),
                AskTimeout);
            Assert.True(reconciliation.IsAccepted, string.Join(Environment.NewLine, reconciliation.Errors));

            var skipped = await coordinator.Ask<SchedulerDispatchResult>(
                new DispatchSchedulerSchedule(Assert.Single(reconciliation.Materializations).Key, firedAtUtc),
                AskTimeout);

            Assert.False(skipped.IsAccepted);
            Assert.Equal(SchedulerLifecycleAuditStage.Skipped, budgetAudit.Records.Last().Stage);
            Assert.Equal(SchedulerLifecycleAuditOutcome.Skipped, budgetAudit.Records.Last().Outcome);
            Assert.Equal("scheduler-proactive-budget-unavailable", budgetAudit.Records.Last().Reason?.Code);
        }
        finally
        {
            await budgetSystem.Terminate();
        }

        var failedStore = new RecordingSchedulerPulseDeliveryStore();
        var failedAudit = new RecordingSchedulerLifecycleAuditSink();
        var failedSystem = ActorSystem.Create("scheduler-coordinator-lifecycle-failed");
        try
        {
            var coordinator = failedSystem.ActorOf(
                SchedulerCoordinator.Props(
                    NoopSchedulerQuartzAdapter.Instance,
                    TimeProvider.System,
                    failedStore,
                    new RecordingSchedulerPulseDispatcher(new InvalidOperationException("Shard failure.")),
                    AllowingSchedulerProactiveBudgetPolicy.Instance,
                    NoopSchedulerReconciliationAuditSink.Instance,
                    failedAudit),
                "scheduler-coordinator-lifecycle-failed");
            var reconciliation = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(failingSnapshot),
                AskTimeout);
            Assert.True(reconciliation.IsAccepted, string.Join(Environment.NewLine, reconciliation.Errors));

            var failed = await coordinator.Ask<SchedulerDispatchResult>(
                new DispatchSchedulerSchedule(Assert.Single(reconciliation.Materializations).Key, firedAtUtc),
                AskTimeout);

            Assert.False(failed.IsAccepted);
            Assert.Equal(SchedulerLifecycleAuditStage.Failed, failedAudit.Records.Last().Stage);
            Assert.Equal(SchedulerLifecycleAuditOutcome.Failed, failedAudit.Records.Last().Outcome);
            Assert.Equal("scheduler-pulse-delivery-failed", failedAudit.Records.Last().Reason?.Code);
        }
        finally
        {
            await failedSystem.Terminate();
        }
    }

    [Fact]
    public async Task Reconcile_emits_lifecycle_skipped_for_missed_window_and_catchup_before_normal_dispatch_events()
    {
        var nonCriticalSnapshot = await ImportedSnapshotAsync(
            WithDeliveryLeadSchedules(
                ExampleConfiguration(),
                new ScheduleEntryConfiguration(
                    "daily-report",
                    "0 55 17 ? * MON-FRI",
                    "Run daily report")),
            new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero));
        var criticalSnapshot = await ImportedSnapshotAsync(
            WithDeliveryLeadSchedules(
                ExampleConfiguration(),
                new ScheduleEntryConfiguration(
                    "critical-report",
                    "0 55 17 ? * MON-FRI",
                    "Run critical report",
                    isCritical: true,
                    catchUp: "catch-up-once")),
            new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero));
        var nowUtc = new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);

        var skippedAudit = new RecordingSchedulerLifecycleAuditSink();
        var skippedSystem = ActorSystem.Create("scheduler-coordinator-lifecycle-missed-skip");
        try
        {
            var coordinator = skippedSystem.ActorOf(
                SchedulerCoordinator.Props(
                    NoopSchedulerQuartzAdapter.Instance,
                    new ManualTimeProvider(nowUtc),
                    new RecordingSchedulerPulseDeliveryStore(),
                    NoopSchedulerPulseDispatcher.Instance,
                    AllowingSchedulerProactiveBudgetPolicy.Instance,
                    NoopSchedulerReconciliationAuditSink.Instance,
                    skippedAudit),
                "scheduler-coordinator-lifecycle-missed-skip");

            var result = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(nonCriticalSnapshot),
                AskTimeout);

            Assert.True(result.IsAccepted, string.Join(Environment.NewLine, result.Errors));
            Assert.Equal(SchedulerLifecycleAuditStage.Skipped, skippedAudit.Records.Last().Stage);
            Assert.Equal("scheduler-missed-window-skipped", skippedAudit.Records.Last().Reason?.Code);
        }
        finally
        {
            await skippedSystem.Terminate();
        }

        var catchUpAudit = new RecordingSchedulerLifecycleAuditSink();
        var catchUpSystem = ActorSystem.Create("scheduler-coordinator-lifecycle-catchup");
        try
        {
            var coordinator = catchUpSystem.ActorOf(
                SchedulerCoordinator.Props(
                    NoopSchedulerQuartzAdapter.Instance,
                    new ManualTimeProvider(nowUtc),
                    new RecordingSchedulerPulseDeliveryStore(),
                    new RecordingSchedulerPulseDispatcher(),
                    AllowingSchedulerProactiveBudgetPolicy.Instance,
                    NoopSchedulerReconciliationAuditSink.Instance,
                    catchUpAudit),
                "scheduler-coordinator-lifecycle-catchup");

            var result = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(criticalSnapshot),
                AskTimeout);

            Assert.True(result.IsAccepted, string.Join(Environment.NewLine, result.Errors));
            Assert.Equal(
                new[]
                {
                    SchedulerLifecycleAuditStage.Materialized,
                    SchedulerLifecycleAuditStage.CatchUp,
                    SchedulerLifecycleAuditStage.Fired,
                    SchedulerLifecycleAuditStage.Delivered,
                },
                catchUpAudit.Records.Select(record => record.Stage));
            Assert.Equal(SchedulerLifecycleAuditSource.CatchUp, catchUpAudit.Records[1].Source);
            Assert.Equal(catchUpAudit.Records[1].IdempotencyKey, catchUpAudit.Records[2].IdempotencyKey);
        }
        finally
        {
            await catchUpSystem.Terminate();
        }
    }

    [Fact]
    public async Task Idempotent_quartz_actor_removes_existing_job_and_treats_missing_job_as_success()
    {
        var key = SchedulerScheduleKey.From(
            OrganizationId.From("acme-delivery"),
            PositionId.From("delivery-lead"),
            ScheduleId.From("daily-report"));
        var job = new SchedulerQuartzJob(
            key,
            DomainCronExpression.From("0 55 17 ? * MON-FRI"),
            "Europe/Lisbon");
        var scheduler = await NewInMemoryQuartzSchedulerAsync();
        var system = ActorSystem.Create("scheduler-coordinator-idempotent-quartz-remove");
        try
        {
            var quartzActor = system.ActorOf(
                Props.Create(() => new HiveQuartzActor(scheduler)),
                "idempotent-quartz-remove");
            var remove = new RemoveJob(
                new JobKey(job.Identity.JobName, job.Identity.JobGroup),
                new TriggerKey(job.Identity.TriggerName, job.Identity.TriggerGroup));

            await quartzActor.Ask<JobCreated>(
                new CreateJob(
                    ActorRefs.Nobody,
                    new SchedulerQuartzScheduleFired(job.Key),
                    AkkaQuartzSchedulerAdapter.BuildTrigger(job)),
                AskTimeout);
            var firstRemoval = await quartzActor.Ask<object>(remove, AskTimeout);
            var secondRemoval = await quartzActor.Ask<object>(remove, AskTimeout);

            Assert.IsType<JobRemoved>(firstRemoval);
            Assert.IsType<JobRemoved>(secondRemoval);
            Assert.Empty(await scheduler.GetJobKeys(
                GroupMatcher<JobKey>.GroupEquals(SchedulerQuartzIdentity.JobGroupName)));
            Assert.Empty(await scheduler.GetTriggerKeys(
                GroupMatcher<TriggerKey>.GroupEquals(SchedulerQuartzIdentity.TriggerGroupName)));
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false);
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Dispatch_skips_non_critical_schedule_outside_working_hours_without_delivery()
    {
        var snapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "end-of-day-report",
                "0 0 18 ? * MON-FRI",
                "Run end of day report")));
        var firedAtUtc = new DateTimeOffset(2026, 7, 3, 17, 0, 0, TimeSpan.Zero);
        var deliveryStore = new RecordingSchedulerPulseDeliveryStore();
        var pulseDispatcher = new RecordingSchedulerPulseDispatcher();
        var system = ActorSystem.Create("scheduler-coordinator-policy-working-hours");
        try
        {
            var coordinator = system.ActorOf(
                SchedulerCoordinator.Props(
                    NoopSchedulerQuartzAdapter.Instance,
                    TimeProvider.System,
                    deliveryStore,
                    pulseDispatcher),
                "scheduler-coordinator-policy-working-hours");
            var reconciliation = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(snapshot),
                AskTimeout);
            Assert.True(reconciliation.IsAccepted, string.Join(Environment.NewLine, reconciliation.Errors));

            var knownKey = Assert.Single(reconciliation.Materializations).Key;
            var skipped = await coordinator.Ask<SchedulerDispatchResult>(
                new DispatchSchedulerSchedule(knownKey, firedAtUtc),
                AskTimeout);

            Assert.False(skipped.IsAccepted);
            Assert.Equal("scheduler-outside-working-hours", skipped.Error?.Code);
            Assert.Single(deliveryStore.Fired);
            var skippedTransition = Assert.Single(deliveryStore.Skipped);
            Assert.Equal("scheduler-outside-working-hours", skippedTransition.Reason!.Code);
            Assert.Empty(pulseDispatcher.Pulses);

            var state = await coordinator.Ask<SchedulerCoordinatorState>(
                GetSchedulerCoordinatorState.Instance,
                AskTimeout);
            Assert.Empty(state.PendingDispatches);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Dispatch_delivers_critical_schedule_outside_working_hours_when_budget_is_available()
    {
        var snapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "critical-report",
                "0 0 18 ? * MON-FRI",
                "Run critical report",
                isCritical: true)));
        var firedAtUtc = new DateTimeOffset(2026, 7, 3, 17, 0, 0, TimeSpan.Zero);
        var deliveryStore = new RecordingSchedulerPulseDeliveryStore();
        var pulseDispatcher = new RecordingSchedulerPulseDispatcher();
        var system = ActorSystem.Create("scheduler-coordinator-policy-critical");
        try
        {
            var coordinator = system.ActorOf(
                SchedulerCoordinator.Props(
                    NoopSchedulerQuartzAdapter.Instance,
                    TimeProvider.System,
                    deliveryStore,
                    pulseDispatcher),
                "scheduler-coordinator-policy-critical");
            var reconciliation = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(snapshot),
                AskTimeout);
            Assert.True(reconciliation.IsAccepted, string.Join(Environment.NewLine, reconciliation.Errors));

            var knownKey = Assert.Single(reconciliation.Materializations).Key;
            var accepted = await coordinator.Ask<SchedulerDispatchResult>(
                new DispatchSchedulerSchedule(knownKey, firedAtUtc),
                AskTimeout);

            Assert.True(accepted.IsAccepted, accepted.Error?.ToString());
            Assert.Single(deliveryStore.Fired);
            Assert.Empty(deliveryStore.Skipped);
            Assert.Equal(accepted.Dispatch!.Pulse, Assert.Single(pulseDispatcher.Pulses));
            Assert.Single(deliveryStore.Delivered);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Dispatch_skips_when_proactive_budget_is_unavailable_without_delivery()
    {
        var snapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "daily-report",
                "0 55 17 ? * MON-FRI",
                "Run daily report")));
        var firedAtUtc = new DateTimeOffset(2026, 7, 3, 16, 55, 0, TimeSpan.Zero);
        var deliveryStore = new RecordingSchedulerPulseDeliveryStore();
        var pulseDispatcher = new RecordingSchedulerPulseDispatcher();
        var budgetPolicy = new RecordingSchedulerProactiveBudgetPolicy(hasAvailableBudget: false);
        var system = ActorSystem.Create("scheduler-coordinator-policy-budget");
        try
        {
            var coordinator = system.ActorOf(
                SchedulerCoordinator.Props(
                    NoopSchedulerQuartzAdapter.Instance,
                    TimeProvider.System,
                    deliveryStore,
                    pulseDispatcher,
                    budgetPolicy),
                "scheduler-coordinator-policy-budget");
            var reconciliation = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(snapshot),
                AskTimeout);
            Assert.True(reconciliation.IsAccepted, string.Join(Environment.NewLine, reconciliation.Errors));

            var knownKey = Assert.Single(reconciliation.Materializations).Key;
            var skipped = await coordinator.Ask<SchedulerDispatchResult>(
                new DispatchSchedulerSchedule(knownKey, firedAtUtc),
                AskTimeout);

            Assert.False(skipped.IsAccepted);
            Assert.Equal("scheduler-proactive-budget-unavailable", skipped.Error?.Code);
            Assert.Single(budgetPolicy.Requests);
            Assert.Single(deliveryStore.Fired);
            var skippedTransition = Assert.Single(deliveryStore.Skipped);
            Assert.Equal("scheduler-proactive-budget-unavailable", skippedTransition.Reason!.Code);
            Assert.Empty(pulseDispatcher.Pulses);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Dispatch_marks_failed_when_sharding_delivery_fails_without_pending_dispatch()
    {
        var snapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "daily-report",
                "0 55 17 ? * MON-FRI",
                "Run daily report")));
        var firedAtUtc = new DateTimeOffset(2026, 7, 3, 17, 10, 0, TimeSpan.Zero);
        var deliveryStore = new RecordingSchedulerPulseDeliveryStore();
        var pulseDispatcher = new RecordingSchedulerPulseDispatcher(
            new InvalidOperationException("Shard region is unavailable."));
        var system = ActorSystem.Create("scheduler-coordinator-delivery-failure");
        try
        {
            var coordinator = system.ActorOf(
                SchedulerCoordinator.Props(
                    NoopSchedulerQuartzAdapter.Instance,
                    TimeProvider.System,
                    deliveryStore,
                    pulseDispatcher),
                "scheduler-coordinator-delivery-failure");
            var reconciliation = await coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(snapshot),
                AskTimeout);
            Assert.True(reconciliation.IsAccepted, string.Join(Environment.NewLine, reconciliation.Errors));

            var knownKey = Assert.Single(reconciliation.Materializations).Key;
            var rejected = await coordinator.Ask<SchedulerDispatchResult>(
                new DispatchSchedulerSchedule(knownKey, firedAtUtc),
                AskTimeout);

            Assert.False(rejected.IsAccepted);
            Assert.Equal("scheduler-pulse-delivery-failed", rejected.Error?.Code);
            Assert.Single(deliveryStore.Fired);
            var failed = Assert.Single(deliveryStore.Failed);
            Assert.Equal("scheduler-pulse-delivery-failed", failed.Reason!.Code);
            Assert.Empty(pulseDispatcher.Pulses);

            var state = await coordinator.Ask<SchedulerCoordinatorState>(
                GetSchedulerCoordinatorState.Instance,
                AskTimeout);
            Assert.Empty(state.PendingDispatches);
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static Pulse BuildSchedulerPulse(
        SchedulerScheduleMaterialization materialization,
        DateTimeOffset firedAtUtc,
        PulseIdempotencyKey idempotencyKey) =>
        SchedulerPulseFactory.Build(materialization, firedAtUtc, idempotencyKey);

    private static SchedulerScheduleDispatch BuildDispatch(
        SchedulerScheduleMaterialization materialization,
        DateTimeOffset firedAtUtc)
    {
        var dispatchWindow = SchedulerScheduleWindowCalculator.Calculate(materialization, firedAtUtc);
        var pulse = BuildSchedulerPulse(
            materialization,
            firedAtUtc,
            dispatchWindow.IdempotencyKey);

        return new SchedulerScheduleDispatch(
            materialization.Key,
            firedAtUtc,
            dispatchWindow.Window,
            dispatchWindow.IdempotencyKey,
            pulse);
    }

    private static Pulse DispatchPulse(SchedulerScheduleDispatch dispatch) => dispatch.Pulse;

    private static string DescribeOperation(SchedulerScheduleReconciliationOperation operation) =>
        $"{operation.Kind}:{operation.Key.Value}";

    private static string DescribeOperation(
        SchedulerScheduleReconciliationOperationKind kind,
        SchedulerScheduleKey key) =>
        $"{kind}:{key.Value}";

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class RecordingSchedulerReconciliationAuditSink : ISchedulerReconciliationAuditSink
    {
        private readonly List<SchedulerReconciliationAuditRecord> _records = [];

        public IReadOnlyList<SchedulerReconciliationAuditRecord> Records => _records;

        public void Publish(SchedulerReconciliationAuditRecord record)
        {
            _records.Add(record);
        }
    }

    private sealed class RecordingSchedulerLifecycleAuditSink : ISchedulerLifecycleAuditSink
    {
        private readonly List<SchedulerLifecycleAuditRecord> _records = [];

        public IReadOnlyList<SchedulerLifecycleAuditRecord> Records => _records;

        public void Publish(SchedulerLifecycleAuditRecord record)
        {
            _records.Add(record);
        }
    }

    private sealed class RecordingSchedulerQuartzAdapter : ISchedulerQuartzAdapter
    {
        private readonly Dictionary<SchedulerQuartzIdentity, SchedulerQuartzJob> _jobs = [];
        private readonly List<SchedulerQuartzJob> _scheduleCalls = [];
        private readonly List<SchedulerScheduleKey> _unscheduleCalls = [];

        public IReadOnlyCollection<SchedulerQuartzJob> Jobs => _jobs.Values;

        public IReadOnlyList<SchedulerQuartzJob> ScheduleCalls => _scheduleCalls;

        public IReadOnlyList<SchedulerScheduleKey> UnscheduleCalls => _unscheduleCalls;

        public void Schedule(IActorContext context, IActorRef receiver, SchedulerQuartzJob job)
        {
            _scheduleCalls.Add(job);
            _jobs[job.Identity] = job;
        }

        public void Unschedule(IActorContext context, SchedulerScheduleKey key)
        {
            _unscheduleCalls.Add(key);
            _jobs.Remove(SchedulerQuartzIdentity.From(key));
        }
    }

    private sealed class RecordingSchedulerPulseDeliveryStore : ISchedulerPulseDeliveryStore
    {
        private readonly List<SchedulerPulseDeliveryRecord> _fired = [];
        private readonly List<DeliveryTransition> _delivered = [];
        private readonly List<DeliveryTransition> _skipped = [];
        private readonly List<DeliveryTransition> _failed = [];
        private readonly Dictionary<PulseIdempotencyKey, SchedulerPulseDeliveryState> _states = [];

        public IReadOnlyList<SchedulerPulseDeliveryRecord> Fired => _fired;

        public IReadOnlyList<DeliveryTransition> Delivered => _delivered;

        public IReadOnlyList<DeliveryTransition> Skipped => _skipped;

        public IReadOnlyList<DeliveryTransition> Failed => _failed;

        public Task<SchedulerPulseDeliveryState> RecordFiredAsync(
            SchedulerPulseDeliveryRecord delivery,
            CancellationToken cancellationToken = default)
        {
            _fired.Add(delivery);
            var existing = _states.GetValueOrDefault(delivery.IdempotencyKey);
            var status = existing is null
                ? SchedulerPulseDeliveryStatus.Fired
                : SchedulerPulseDeliveryStatus.Redelivered;
            var attemptCount = existing?.AttemptCount + 1 ?? 1;
            var state = new SchedulerPulseDeliveryState(
                delivery.IdempotencyKey,
                delivery.MessageId,
                delivery.ThreadId,
                status,
                attemptCount,
                delivery.OccurredAtUtc,
                reason: null);
            _states[delivery.IdempotencyKey] = state;
            return Task.FromResult(state);
        }

        public Task<SchedulerPulseDeliveryState> RecordSkippedAsync(
            SchedulerPulseDeliveryRecord delivery,
            SchedulerPulseDeliveryReason reason,
            CancellationToken cancellationToken = default)
        {
            var state = new SchedulerPulseDeliveryState(
                delivery.IdempotencyKey,
                delivery.MessageId,
                delivery.ThreadId,
                SchedulerPulseDeliveryStatus.Skipped,
                attemptCount: 1,
                delivery.OccurredAtUtc,
                reason);
            _skipped.Add(new DeliveryTransition(delivery.IdempotencyKey, delivery.OccurredAtUtc, reason));
            _states[delivery.IdempotencyKey] = state;
            return Task.FromResult(state);
        }

        public Task<SchedulerPulseDeliveryState> MarkDeliveredAsync(
            PulseIdempotencyKey idempotencyKey,
            DateTimeOffset occurredAtUtc,
            SchedulerPulseDeliveryReason? reason = null,
            CancellationToken cancellationToken = default)
        {
            var fired = _fired.Last(delivery => delivery.IdempotencyKey == idempotencyKey);
            var attemptCount = _states.GetValueOrDefault(idempotencyKey)?.AttemptCount ?? 1;
            _delivered.Add(new DeliveryTransition(idempotencyKey, occurredAtUtc, reason));
            var state = new SchedulerPulseDeliveryState(
                idempotencyKey,
                fired.MessageId,
                fired.ThreadId,
                SchedulerPulseDeliveryStatus.Delivered,
                attemptCount,
                occurredAtUtc,
                reason);
            _states[idempotencyKey] = state;
            return Task.FromResult(state);
        }

        public Task<SchedulerPulseDeliveryState> MarkSkippedAsync(
            PulseIdempotencyKey idempotencyKey,
            DateTimeOffset occurredAtUtc,
            SchedulerPulseDeliveryReason reason,
            CancellationToken cancellationToken = default)
        {
            var fired = _fired.Last(delivery => delivery.IdempotencyKey == idempotencyKey);
            var attemptCount = _states.GetValueOrDefault(idempotencyKey)?.AttemptCount ?? 1;
            _skipped.Add(new DeliveryTransition(idempotencyKey, occurredAtUtc, reason));
            var state = new SchedulerPulseDeliveryState(
                idempotencyKey,
                fired.MessageId,
                fired.ThreadId,
                SchedulerPulseDeliveryStatus.Skipped,
                attemptCount,
                occurredAtUtc,
                reason);
            _states[idempotencyKey] = state;
            return Task.FromResult(state);
        }

        public Task<SchedulerPulseDeliveryState> MarkFailedAsync(
            PulseIdempotencyKey idempotencyKey,
            DateTimeOffset occurredAtUtc,
            SchedulerPulseDeliveryReason reason,
            CancellationToken cancellationToken = default)
        {
            var fired = _fired.Last(delivery => delivery.IdempotencyKey == idempotencyKey);
            var attemptCount = _states.GetValueOrDefault(idempotencyKey)?.AttemptCount ?? 1;
            _failed.Add(new DeliveryTransition(idempotencyKey, occurredAtUtc, reason));
            var state = new SchedulerPulseDeliveryState(
                idempotencyKey,
                fired.MessageId,
                fired.ThreadId,
                SchedulerPulseDeliveryStatus.Failed,
                attemptCount,
                occurredAtUtc,
                reason);
            _states[idempotencyKey] = state;
            return Task.FromResult(state);
        }

        public Task<SchedulerPulseDeliveryState?> FindAsync(
            PulseIdempotencyKey idempotencyKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_states.GetValueOrDefault(idempotencyKey));

        public Task<IReadOnlyList<SchedulerPulseDeliveryHistoryEntry>> ReadHistoryAsync(
            PulseIdempotencyKey idempotencyKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingSchedulerPulseDispatcher(Exception? failure = null) : ISchedulerPulseDispatcher
    {
        private readonly List<Pulse> _pulses = [];

        public IReadOnlyList<Pulse> Pulses => _pulses;

        public Task DeliverAsync(
            IActorContext context,
            Pulse pulse,
            CancellationToken cancellationToken = default)
        {
            if (failure is not null)
            {
                throw failure;
            }

            _pulses.Add(pulse);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSchedulerProactiveBudgetPolicy(bool hasAvailableBudget)
        : ISchedulerProactiveBudgetPolicy
    {
        private readonly List<SchedulerProactiveBudgetRequest> _requests = [];

        public IReadOnlyList<SchedulerProactiveBudgetRequest> Requests => _requests;

        public Task<bool> HasAvailableBudgetAsync(
            SchedulerProactiveBudgetRequest request,
            CancellationToken cancellationToken = default)
        {
            _requests.Add(request);
            return Task.FromResult(hasAvailableBudget);
        }
    }

    private sealed record DeliveryTransition(
        PulseIdempotencyKey IdempotencyKey,
        DateTimeOffset OccurredAtUtc,
        SchedulerPulseDeliveryReason? Reason);
}
