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
