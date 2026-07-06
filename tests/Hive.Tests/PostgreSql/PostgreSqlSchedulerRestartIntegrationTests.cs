using Hive.Actors.Scheduling;
using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using Hive.Domain.Scheduling;
using Hive.Infrastructure.Organization.Registry;
using Hive.Infrastructure.Scheduling;

namespace Hive.Tests.PostgreSql;

[Collection(AkkaPostgreSqlCollection.Name)]
public sealed class PostgreSqlSchedulerRestartIntegrationTests(PostgreSqlFixture postgres)
{
    [Fact]
    public async Task Repeated_reconciliation_before_and_after_restart_keeps_single_quartz_identity_and_materialized_audit_per_lifecycle()
    {
        var reconcileAtUtc = new DateTimeOffset(2026, 7, 3, 16, 50, 0, TimeSpan.Zero);
        OrganizationRegistrySnapshot snapshot = null!;

        await using (var fixture = await SchedulerIntegrationFixture.StartAsync(postgres.ConnectionString))
        {
            fixture.Clock.SetUtcNow(reconcileAtUtc);
            snapshot = await SchedulerIntegrationFixture.ImportedSnapshotAsync(
                new ScheduleEntryConfiguration(
                    "no-duplicate-report",
                    "0 55 17 ? * MON-FRI",
                    "Run no duplicate report"),
                reconcileAtUtc);

            var first = await fixture.ReconcileAsync(snapshot);
            var repeated = await fixture.ReconcileAsync(snapshot);

            Assert.True(first.IsAccepted, string.Join(Environment.NewLine, first.Errors));
            Assert.True(repeated.IsAccepted, string.Join(Environment.NewLine, repeated.Errors));
            Assert.False(repeated.Diff.IsRegistryChanged);
            Assert.Empty(repeated.Diff.Operations);

            var materialization = Assert.Single(first.Materializations);
            await fixture.WaitForQuartzJobAsync(materialization.Key);

            var jobKeys = await fixture.ReadQuartzJobKeysAsync();
            var triggerKeys = await fixture.ReadQuartzTriggerKeysAsync();
            Assert.Single(jobKeys);
            Assert.Single(triggerKeys);
            Assert.Equal(SchedulerQuartzIdentity.From(materialization.Key).JobName, jobKeys[0].Name);
            Assert.Equal(SchedulerQuartzIdentity.From(materialization.Key).TriggerName, triggerKeys[0].Name);
            Assert.Equal(1, fixture.LifecycleAuditRecords.Count(
                record => record.Stage == SchedulerLifecycleAuditStage.Materialized));
        }

        await using (var restarted = await SchedulerIntegrationFixture.StartAsync(
            postgres.ConnectionString,
            resetSchemas: false))
        {
            restarted.Clock.SetUtcNow(reconcileAtUtc);

            var firstAfterRestart = await restarted.ReconcileAsync(snapshot);
            var repeatedAfterRestart = await restarted.ReconcileAsync(snapshot);

            Assert.True(
                firstAfterRestart.IsAccepted,
                string.Join(Environment.NewLine, firstAfterRestart.Errors));
            Assert.True(
                repeatedAfterRestart.IsAccepted,
                string.Join(Environment.NewLine, repeatedAfterRestart.Errors));
            Assert.False(repeatedAfterRestart.Diff.IsRegistryChanged);
            Assert.Empty(repeatedAfterRestart.Diff.Operations);

            var materialization = Assert.Single(firstAfterRestart.Materializations);
            await restarted.WaitForQuartzJobAsync(materialization.Key);

            var jobKeys = await restarted.ReadQuartzJobKeysAsync();
            var triggerKeys = await restarted.ReadQuartzTriggerKeysAsync();
            Assert.Single(jobKeys);
            Assert.Single(triggerKeys);
            Assert.Equal(SchedulerQuartzIdentity.From(materialization.Key).JobName, jobKeys[0].Name);
            Assert.Equal(SchedulerQuartzIdentity.From(materialization.Key).TriggerName, triggerKeys[0].Name);
            Assert.Equal(1, restarted.LifecycleAuditRecords.Count(
                record => record.Stage == SchedulerLifecycleAuditStage.Materialized));
        }
    }

    [Fact]
    public async Task Delivered_window_redelivers_after_scheduler_restart_without_duplicate_position_work()
    {
        var firedAtUtc = new DateTimeOffset(2026, 7, 3, 17, 10, 0, TimeSpan.Zero);
        OrganizationRegistrySnapshot snapshot = null!;
        PositionEntityId entity = null!;
        PulseIdempotencyKey idempotencyKey = null!;
        MessageId firstMessageId = null!;
        ThreadId firstThreadId = null!;

        await using (var fixture = await SchedulerIntegrationFixture.StartAsync(postgres.ConnectionString))
        {
            fixture.Clock.SetUtcNow(new DateTimeOffset(2026, 7, 3, 16, 50, 0, TimeSpan.Zero));
            snapshot = await SchedulerIntegrationFixture.ImportedSnapshotAsync(
                new ScheduleEntryConfiguration(
                    "restart-report",
                    "0 55 17 ? * MON-FRI",
                    "Run restart report"),
                fixture.Clock.GetUtcNow());

            var reconciliation = await fixture.ReconcileAsync(snapshot);

            Assert.True(reconciliation.IsAccepted, string.Join(Environment.NewLine, reconciliation.Errors));
            var materialization = Assert.Single(reconciliation.Materializations);
            entity = PositionEntityId.From(materialization.Key.Organization, materialization.Key.Position);
            idempotencyKey = SchedulerScheduleWindowCalculator
                .Calculate(materialization, firedAtUtc)
                .IdempotencyKey;
            fixture.Clock.SetUtcNow(firedAtUtc);

            await fixture.TriggerQuartzAsync(materialization.Key);

            var delivery = await fixture.WaitForDeliveryAsync(
                idempotencyKey,
                SchedulerPulseDeliveryStatus.Delivered);
            await fixture.WaitForMessageReceivedAsync(entity, delivery.MessageId);
            firstMessageId = delivery.MessageId;
            firstThreadId = delivery.ThreadId;

            var firstHistory = await fixture.ReadDeliveryHistoryAsync(idempotencyKey);
            Assert.Equal(
                [
                    SchedulerPulseDeliveryStatus.Registered,
                    SchedulerPulseDeliveryStatus.Fired,
                    SchedulerPulseDeliveryStatus.Delivered,
                ],
                firstHistory.Select(entry => entry.Status));
        }

        await using (var restarted = await SchedulerIntegrationFixture.StartAsync(
            postgres.ConnectionString,
            resetSchemas: false))
        {
            restarted.Clock.SetUtcNow(firedAtUtc);
            var reconciliation = await restarted.ReconcileAsync(snapshot);

            Assert.True(reconciliation.IsAccepted, string.Join(Environment.NewLine, reconciliation.Errors));
            var materialization = Assert.Single(reconciliation.Materializations);

            await restarted.TriggerQuartzAsync(materialization.Key);
            await restarted.WaitForDuplicateRejectedAsync(entity, firstMessageId);

            var redelivered = await restarted.FindDeliveryAsync(idempotencyKey);
            Assert.NotNull(redelivered);
            Assert.Equal(SchedulerPulseDeliveryStatus.Delivered, redelivered.Status);
            Assert.Equal(2, redelivered.AttemptCount);
            Assert.Equal(firstMessageId, redelivered.MessageId);
            Assert.Equal(firstThreadId, redelivered.ThreadId);
            Assert.Empty(restarted.CommittedMessages(entity, firstMessageId));

            var history = await restarted.ReadDeliveryHistoryAsync(idempotencyKey);
            Assert.Equal(
                [
                    SchedulerPulseDeliveryStatus.Registered,
                    SchedulerPulseDeliveryStatus.Fired,
                    SchedulerPulseDeliveryStatus.Delivered,
                    SchedulerPulseDeliveryStatus.Redelivered,
                    SchedulerPulseDeliveryStatus.Delivered,
                ],
                history.Select(entry => entry.Status));
        }
    }

    [Fact]
    public async Task Reprocessing_same_window_after_restart_keeps_single_delivery_row_and_single_position_message()
    {
        var reconcileAtUtc = new DateTimeOffset(2026, 7, 3, 16, 50, 0, TimeSpan.Zero);
        var firedAtUtc = new DateTimeOffset(2026, 7, 3, 17, 10, 0, TimeSpan.Zero);
        OrganizationRegistrySnapshot snapshot = null!;
        PositionEntityId entity = null!;
        PulseIdempotencyKey idempotencyKey = null!;
        MessageId firstMessageId = null!;
        ThreadId firstThreadId = null!;

        await using (var fixture = await SchedulerIntegrationFixture.StartAsync(postgres.ConnectionString))
        {
            fixture.Clock.SetUtcNow(reconcileAtUtc);
            snapshot = await SchedulerIntegrationFixture.ImportedSnapshotAsync(
                new ScheduleEntryConfiguration(
                    "idempotent-reprocess-report",
                    "0 55 17 ? * MON-FRI",
                    "Run idempotent reprocess report"),
                reconcileAtUtc);

            var reconciliation = await fixture.ReconcileAsync(snapshot);

            Assert.True(reconciliation.IsAccepted, string.Join(Environment.NewLine, reconciliation.Errors));
            var materialization = Assert.Single(reconciliation.Materializations);
            entity = PositionEntityId.From(materialization.Key.Organization, materialization.Key.Position);
            idempotencyKey = SchedulerScheduleWindowCalculator
                .Calculate(materialization, firedAtUtc)
                .IdempotencyKey;
            fixture.Clock.SetUtcNow(firedAtUtc);

            await fixture.TriggerQuartzAsync(materialization.Key);

            var delivery = await fixture.WaitForDeliveryAttemptAsync(
                idempotencyKey,
                SchedulerPulseDeliveryStatus.Delivered,
                attemptCount: 1);
            await fixture.WaitForMessageReceivedAsync(entity, delivery.MessageId);
            firstMessageId = delivery.MessageId;
            firstThreadId = delivery.ThreadId;
        }

        await using (var restarted = await SchedulerIntegrationFixture.StartAsync(
            postgres.ConnectionString,
            resetSchemas: false))
        {
            restarted.Clock.SetUtcNow(firedAtUtc);
            var reconciliation = await restarted.ReconcileAsync(snapshot);

            Assert.True(reconciliation.IsAccepted, string.Join(Environment.NewLine, reconciliation.Errors));
            var materialization = Assert.Single(reconciliation.Materializations);

            await restarted.TriggerQuartzAsync(materialization.Key);
            var second = await restarted.WaitForDeliveryAttemptAsync(
                idempotencyKey,
                SchedulerPulseDeliveryStatus.Delivered,
                attemptCount: 2);
            await restarted.WaitForDuplicateRejectedCountAsync(entity, firstMessageId, expectedCount: 1);
            await restarted.TriggerQuartzAsync(materialization.Key);
            var third = await restarted.WaitForDeliveryAttemptAsync(
                idempotencyKey,
                SchedulerPulseDeliveryStatus.Delivered,
                attemptCount: 3);
            await restarted.WaitForDuplicateRejectedCountAsync(entity, firstMessageId, expectedCount: 2);

            Assert.Equal(firstMessageId, second.MessageId);
            Assert.Equal(firstMessageId, third.MessageId);
            Assert.Equal(firstThreadId, second.ThreadId);
            Assert.Equal(firstThreadId, third.ThreadId);
            Assert.Empty(restarted.CommittedMessages(entity, firstMessageId));

            var history = await restarted.ReadDeliveryHistoryAsync(idempotencyKey);
            Assert.Equal(
                [
                    SchedulerPulseDeliveryStatus.Registered,
                    SchedulerPulseDeliveryStatus.Fired,
                    SchedulerPulseDeliveryStatus.Delivered,
                    SchedulerPulseDeliveryStatus.Redelivered,
                    SchedulerPulseDeliveryStatus.Delivered,
                    SchedulerPulseDeliveryStatus.Redelivered,
                    SchedulerPulseDeliveryStatus.Delivered,
                ],
                history.Select(entry => entry.Status));
        }
    }

    [Fact]
    public async Task Reconfiguration_replaces_changed_schedule_without_duplicate_identity_or_unchanged_audit()
    {
        var reconcileAtUtc = new DateTimeOffset(2026, 7, 3, 16, 50, 0, TimeSpan.Zero);

        await using var fixture = await SchedulerIntegrationFixture.StartAsync(postgres.ConnectionString);
        fixture.Clock.SetUtcNow(reconcileAtUtc);
        var initialSnapshot = await SchedulerIntegrationFixture.ImportedSnapshotAsync(
            new ScheduleEntryConfiguration(
                "stable-report",
                "0 55 17 ? * MON-FRI",
                "Run stable report"),
            new ScheduleEntryConfiguration(
                "changed-report",
                "0 55 17 ? * MON-FRI",
                "Run original changed report"),
            reconcileAtUtc);
        var updatedSnapshot = await SchedulerIntegrationFixture.ImportedSnapshotAsync(
            new ScheduleEntryConfiguration(
                "stable-report",
                "0 55 17 ? * MON-FRI",
                "Run stable report"),
            new ScheduleEntryConfiguration(
                "changed-report",
                "0 5 17 ? * MON-FRI",
                "Run updated changed report"),
            reconcileAtUtc);

        var initial = await fixture.ReconcileAsync(initialSnapshot);
        var updated = await fixture.ReconcileAsync(updatedSnapshot);

        Assert.True(initial.IsAccepted, string.Join(Environment.NewLine, initial.Errors));
        Assert.True(updated.IsAccepted, string.Join(Environment.NewLine, updated.Errors));
        Assert.Equal(2, initial.Materializations.Length);
        Assert.Equal(2, updated.Materializations.Length);

        var operationsBySchedule = updated.Diff.Operations.ToDictionary(
            operation => operation.Key.Schedule.Value,
            StringComparer.Ordinal);
        Assert.Equal(
            SchedulerScheduleReconciliationOperationKind.Unchanged,
            operationsBySchedule["stable-report"].Kind);
        Assert.Equal(
            SchedulerScheduleReconciliationOperationKind.Update,
            operationsBySchedule["changed-report"].Kind);

        foreach (var materialization in updated.Materializations)
        {
            await fixture.WaitForQuartzJobAsync(materialization.Key);
        }

        var jobKeys = await fixture.ReadQuartzJobKeysAsync();
        var triggerKeys = await fixture.ReadQuartzTriggerKeysAsync();
        Assert.Equal(2, jobKeys.Count);
        Assert.Equal(2, triggerKeys.Count);
        Assert.Equal(2, jobKeys.Select(key => key.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, triggerKeys.Select(key => key.Name).Distinct(StringComparer.Ordinal).Count());

        var materialized = fixture.LifecycleAuditRecords
            .Where(record => record.Stage == SchedulerLifecycleAuditStage.Materialized)
            .ToArray();
        Assert.Equal(3, materialized.Length);
        Assert.Equal(1, materialized.Count(record => record.ScheduleId?.Value == "stable-report"));
        Assert.Equal(2, materialized.Count(record => record.ScheduleId?.Value == "changed-report"));
    }

    [Fact]
    public async Task Non_critical_missed_window_is_skipped_without_sharded_delivery()
    {
        var importAtUtc = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
        var restartAtUtc = new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);

        await using var fixture = await SchedulerIntegrationFixture.StartAsync(postgres.ConnectionString);
        fixture.Clock.SetUtcNow(restartAtUtc);
        var snapshot = await SchedulerIntegrationFixture.ImportedSnapshotAsync(
            new ScheduleEntryConfiguration(
                "missed-report",
                "0 55 17 ? * MON-FRI",
                "Run missed report"),
            importAtUtc);

        var reconciliation = await fixture.ReconcileAsync(snapshot);

        Assert.True(reconciliation.IsAccepted, string.Join(Environment.NewLine, reconciliation.Errors));
        var materialization = Assert.Single(reconciliation.Materializations);
        var idempotencyKey = SchedulerScheduleWindowCalculator
            .Calculate(materialization, restartAtUtc)
            .IdempotencyKey;
        var skipped = await fixture.WaitForDeliveryAsync(
            idempotencyKey,
            SchedulerPulseDeliveryStatus.Skipped);

        Assert.Equal(1, skipped.AttemptCount);
        Assert.Equal("scheduler-missed-window-skipped", skipped.Reason?.Code);
        Assert.Empty(fixture.CommittedMessages(
            PositionEntityId.From(materialization.Key.Organization, materialization.Key.Position)));

        var history = await fixture.ReadDeliveryHistoryAsync(idempotencyKey);
        Assert.Equal(
            [
                SchedulerPulseDeliveryStatus.Registered,
                SchedulerPulseDeliveryStatus.Skipped,
            ],
            history.Select(entry => entry.Status));
    }

    [Fact]
    public async Task Critical_catch_up_runs_once_and_is_not_repeated_after_restart()
    {
        var importAtUtc = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
        var restartAtUtc = new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);
        OrganizationRegistrySnapshot snapshot = null!;
        PositionEntityId entity = null!;
        PulseIdempotencyKey idempotencyKey = null!;
        MessageId deliveredMessageId = null!;

        await using (var fixture = await SchedulerIntegrationFixture.StartAsync(postgres.ConnectionString))
        {
            fixture.Clock.SetUtcNow(restartAtUtc);
            snapshot = await SchedulerIntegrationFixture.ImportedSnapshotAsync(
                new ScheduleEntryConfiguration(
                    "critical-catch-up",
                    "0 55 17 ? * MON-FRI",
                    "Run critical catch up",
                    isCritical: true,
                    catchUp: "catch-up-once"),
                importAtUtc);

            var reconciliation = await fixture.ReconcileAsync(snapshot);

            Assert.True(reconciliation.IsAccepted, string.Join(Environment.NewLine, reconciliation.Errors));
            var materialization = Assert.Single(reconciliation.Materializations);
            entity = PositionEntityId.From(materialization.Key.Organization, materialization.Key.Position);
            idempotencyKey = SchedulerScheduleWindowCalculator
                .Calculate(materialization, restartAtUtc)
                .IdempotencyKey;

            var delivered = await fixture.WaitForDeliveryAsync(
                idempotencyKey,
                SchedulerPulseDeliveryStatus.Delivered);
            deliveredMessageId = delivered.MessageId;
            await fixture.WaitForMessageReceivedAsync(entity, delivered.MessageId);

            var firstHistory = await fixture.ReadDeliveryHistoryAsync(idempotencyKey);
            Assert.Equal(
                [
                    SchedulerPulseDeliveryStatus.Registered,
                    SchedulerPulseDeliveryStatus.Fired,
                    SchedulerPulseDeliveryStatus.Delivered,
                ],
                firstHistory.Select(entry => entry.Status));
        }

        await using (var restarted = await SchedulerIntegrationFixture.StartAsync(
            postgres.ConnectionString,
            resetSchemas: false))
        {
            restarted.Clock.SetUtcNow(restartAtUtc.AddMinutes(5));

            var reconciliation = await restarted.ReconcileAsync(snapshot);

            Assert.True(reconciliation.IsAccepted, string.Join(Environment.NewLine, reconciliation.Errors));
            var state = await restarted.FindDeliveryAsync(idempotencyKey);
            Assert.NotNull(state);
            Assert.Equal(SchedulerPulseDeliveryStatus.Delivered, state.Status);
            Assert.Equal(1, state.AttemptCount);
            Assert.Empty(restarted.CommittedMessages(entity, deliveredMessageId));

            var history = await restarted.ReadDeliveryHistoryAsync(idempotencyKey);
            Assert.Equal(
                [
                    SchedulerPulseDeliveryStatus.Registered,
                    SchedulerPulseDeliveryStatus.Fired,
                    SchedulerPulseDeliveryStatus.Delivered,
                ],
                history.Select(entry => entry.Status));
        }
    }
}
