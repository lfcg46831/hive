using Hive.Actors.Scheduling;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using Hive.Infrastructure.Scheduling;

namespace Hive.Tests.PostgreSql;

[Collection(AkkaPostgreSqlCollection.Name)]
public sealed class PostgreSqlSchedulerIntegrationFixtureTests(PostgreSqlFixture postgres)
{
    [Fact]
    public async Task Fixture_delivers_controlled_quartz_pulse_through_postgresql_and_sharded_position()
    {
        await using var fixture = await SchedulerIntegrationFixture.StartAsync(postgres.ConnectionString);
        fixture.Clock.SetUtcNow(new DateTimeOffset(2026, 7, 3, 16, 50, 0, TimeSpan.Zero));
        var snapshot = await SchedulerIntegrationFixture.ImportedSnapshotAsync(
            new ScheduleEntryConfiguration(
                "fixture-report",
                "0 55 17 ? * MON-FRI",
                "Run fixture report"),
            fixture.Clock.GetUtcNow());

        var reconciliation = await fixture.ReconcileAsync(snapshot);

        Assert.True(reconciliation.IsAccepted, string.Join(Environment.NewLine, reconciliation.Errors));
        var materialization = Assert.Single(reconciliation.Materializations);
        await fixture.WaitForQuartzJobAsync(materialization.Key);

        var firedAtUtc = new DateTimeOffset(2026, 7, 3, 17, 10, 0, TimeSpan.Zero);
        var expectedWindow = SchedulerScheduleWindowCalculator.Calculate(materialization, firedAtUtc);
        fixture.Clock.SetUtcNow(firedAtUtc);

        await fixture.TriggerQuartzAsync(materialization.Key);

        var delivery = await fixture.WaitForDeliveryAsync(
            expectedWindow.IdempotencyKey,
            SchedulerPulseDeliveryStatus.Delivered);
        var entity = PositionEntityId.From(materialization.Key.Organization, materialization.Key.Position);
        var received = await fixture.WaitForMessageReceivedAsync(entity, delivery.MessageId);

        var pulse = Assert.IsType<Pulse>(received.Message);
        Assert.Equal(delivery.MessageId, pulse.Id);
        Assert.Equal(delivery.ThreadId, pulse.Thread);
        Assert.Equal(materialization.Key.Schedule.Value, pulse.ScheduleId);
    }
}
