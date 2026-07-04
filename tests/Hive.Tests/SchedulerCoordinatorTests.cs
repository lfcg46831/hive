using Akka.Actor;
using Hive.Actors.Scheduling;
using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;
using Hive.Infrastructure.Organization.Configuration;
using Hive.Infrastructure.Organization.Registry;

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
    public async Task Reconcile_materializes_only_active_schedules_in_deterministic_order()
    {
        var snapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "zeta-report",
                "0 55 17 * * MON-FRI",
                "Run zeta report"),
            new ScheduleEntryConfiguration(
                "paused-report",
                "0 0 12 * * MON-FRI",
                "Do not materialize this",
                isActive: false),
            new ScheduleEntryConfiguration(
                "alpha-report",
                "0 0 9 * * MON-FRI",
                "Run alpha report")));
        var system = ActorSystem.Create("scheduler-coordinator-reconcile");
        try
        {
            var coordinator = system.ActorOf(
                SchedulerCoordinator.Props(),
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
            Assert.Equal("0 0 9 * * MON-FRI", alpha.Definition.Cron.Value);
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
    public async Task Reconcile_rejects_invalid_snapshots_without_replacing_current_materialization()
    {
        var validSnapshot = await ImportedSnapshotAsync(WithDeliveryLeadSchedules(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "daily-report",
                "0 55 17 * * MON-FRI",
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
                SchedulerCoordinator.Props(),
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
                "0 55 17 * * MON-FRI",
                "Run daily report")));
        var firedAtUtc = new DateTimeOffset(2026, 7, 4, 17, 55, 0, TimeSpan.Zero);
        var system = ActorSystem.Create("scheduler-coordinator-dispatch");
        try
        {
            var coordinator = system.ActorOf(
                SchedulerCoordinator.Props(),
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

            var state = await coordinator.Ask<SchedulerCoordinatorState>(
                GetSchedulerCoordinatorState.Instance,
                AskTimeout);
            var pending = Assert.Single(state.PendingDispatches);
            Assert.Equal(knownKey, pending.Key);
            Assert.Equal(firedAtUtc, pending.FiredAtUtc);
        }
        finally
        {
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

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
