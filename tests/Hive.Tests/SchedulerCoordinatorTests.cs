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
        OrganizationConfiguration configuration)
    {
        var registry = new InMemoryOrganizationRegistry();
        var imported = await new OrganizationConfigurationImporter(
            registry,
            new ManualTimeProvider(ImportAt))
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
        LoadedScheduleWorkingHours? workingHours = null)
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
            CatchUpPolicy.Skip);

        return new SchedulerScheduleMaterialization(
            key,
            definition,
            workingHours ?? new LoadedScheduleWorkingHours(new TimeOnly(9, 0), new TimeOnly(18, 0)));
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

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class RecordingSchedulerQuartzAdapter : ISchedulerQuartzAdapter
    {
        private readonly Dictionary<SchedulerQuartzIdentity, SchedulerQuartzJob> _jobs = [];

        public IReadOnlyCollection<SchedulerQuartzJob> Jobs => _jobs.Values;

        public void Schedule(IActorContext context, IActorRef receiver, SchedulerQuartzJob job)
        {
            _jobs[job.Identity] = job;
        }
    }

    private sealed class RecordingSchedulerPulseDeliveryStore : ISchedulerPulseDeliveryStore
    {
        private readonly List<SchedulerPulseDeliveryRecord> _fired = [];
        private readonly List<DeliveryTransition> _delivered = [];
        private readonly List<DeliveryTransition> _skipped = [];
        private readonly List<DeliveryTransition> _failed = [];

        public IReadOnlyList<SchedulerPulseDeliveryRecord> Fired => _fired;

        public IReadOnlyList<DeliveryTransition> Delivered => _delivered;

        public IReadOnlyList<DeliveryTransition> Skipped => _skipped;

        public IReadOnlyList<DeliveryTransition> Failed => _failed;

        public Task<SchedulerPulseDeliveryState> RecordFiredAsync(
            SchedulerPulseDeliveryRecord delivery,
            CancellationToken cancellationToken = default)
        {
            _fired.Add(delivery);
            return Task.FromResult(new SchedulerPulseDeliveryState(
                delivery.IdempotencyKey,
                delivery.MessageId,
                delivery.ThreadId,
                SchedulerPulseDeliveryStatus.Fired,
                attemptCount: 1,
                delivery.OccurredAtUtc,
                reason: null));
        }

        public Task<SchedulerPulseDeliveryState> MarkDeliveredAsync(
            PulseIdempotencyKey idempotencyKey,
            DateTimeOffset occurredAtUtc,
            SchedulerPulseDeliveryReason? reason = null,
            CancellationToken cancellationToken = default)
        {
            var fired = _fired.Single(delivery => delivery.IdempotencyKey == idempotencyKey);
            _delivered.Add(new DeliveryTransition(idempotencyKey, occurredAtUtc, reason));
            return Task.FromResult(new SchedulerPulseDeliveryState(
                idempotencyKey,
                fired.MessageId,
                fired.ThreadId,
                SchedulerPulseDeliveryStatus.Delivered,
                attemptCount: 1,
                occurredAtUtc,
                reason));
        }

        public Task<SchedulerPulseDeliveryState> MarkSkippedAsync(
            PulseIdempotencyKey idempotencyKey,
            DateTimeOffset occurredAtUtc,
            SchedulerPulseDeliveryReason reason,
            CancellationToken cancellationToken = default)
        {
            var fired = _fired.Single(delivery => delivery.IdempotencyKey == idempotencyKey);
            _skipped.Add(new DeliveryTransition(idempotencyKey, occurredAtUtc, reason));
            return Task.FromResult(new SchedulerPulseDeliveryState(
                idempotencyKey,
                fired.MessageId,
                fired.ThreadId,
                SchedulerPulseDeliveryStatus.Skipped,
                attemptCount: 1,
                occurredAtUtc,
                reason));
        }

        public Task<SchedulerPulseDeliveryState> MarkFailedAsync(
            PulseIdempotencyKey idempotencyKey,
            DateTimeOffset occurredAtUtc,
            SchedulerPulseDeliveryReason reason,
            CancellationToken cancellationToken = default)
        {
            var fired = _fired.Single(delivery => delivery.IdempotencyKey == idempotencyKey);
            _failed.Add(new DeliveryTransition(idempotencyKey, occurredAtUtc, reason));
            return Task.FromResult(new SchedulerPulseDeliveryState(
                idempotencyKey,
                fired.MessageId,
                fired.ThreadId,
                SchedulerPulseDeliveryStatus.Failed,
                attemptCount: 1,
                occurredAtUtc,
                reason));
        }

        public Task<SchedulerPulseDeliveryState?> FindAsync(
            PulseIdempotencyKey idempotencyKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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
