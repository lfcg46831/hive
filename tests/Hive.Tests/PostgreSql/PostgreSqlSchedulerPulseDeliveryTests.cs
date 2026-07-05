using Hive.Infrastructure.Scheduling.PostgreSql;
using Hive.Domain.Identity;
using Hive.Domain.Scheduling;
using Hive.Infrastructure.Scheduling;

namespace Hive.Tests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlSchedulerPulseDeliveryTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Migration_creates_scheduler_delivery_schema_and_is_idempotent()
    {
        await using var dataSource = fixture.CreateDataSource();
        await ResetSchedulerAsync(dataSource);
        var migrator = new PostgreSqlSchedulerPulseDeliveryMigrator(dataSource);

        await migrator.MigrateAsync();
        await migrator.MigrateAsync();

        var tableNames = new List<string>();
        await using (var command = dataSource.CreateCommand(
            """
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'scheduler'
            ORDER BY table_name;
            """))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tableNames.Add(reader.GetString(0));
            }
        }

        var appliedVersions = new List<int>();
        await using (var command = dataSource.CreateCommand(
            "SELECT version FROM scheduler.schema_migrations ORDER BY version;"))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                appliedVersions.Add(reader.GetInt32(0));
            }
        }

        Assert.Equal(
            [
                "pulse_deliveries",
                "pulse_delivery_history",
                "schema_migrations",
            ],
            tableNames);
        Assert.Equal([1], appliedVersions);
    }

    [Fact]
    public async Task Store_records_fired_dispatch_and_redelivery_history()
    {
        await using var dataSource = fixture.CreateDataSource();
        await ResetSchedulerAsync(dataSource);
        await new PostgreSqlSchedulerPulseDeliveryMigrator(dataSource).MigrateAsync();
        var store = new PostgreSqlSchedulerPulseDeliveryStore(dataSource);
        var delivery = DeliveryRecord();

        var first = await store.RecordFiredAsync(delivery);
        var second = await store.RecordFiredAsync(delivery);

        Assert.Equal(SchedulerPulseDeliveryStatus.Fired, first.Status);
        Assert.Equal(1, first.AttemptCount);
        Assert.Equal(SchedulerPulseDeliveryStatus.Redelivered, second.Status);
        Assert.Equal(2, second.AttemptCount);
        Assert.Equal(delivery.IdempotencyKey, second.IdempotencyKey);
        Assert.Equal(delivery.MessageId, second.MessageId);
        Assert.Equal(delivery.ThreadId, second.ThreadId);

        var reloaded = await store.FindAsync(delivery.IdempotencyKey);
        Assert.NotNull(reloaded);
        Assert.Equal(SchedulerPulseDeliveryStatus.Redelivered, reloaded.Status);
        Assert.Equal(2, reloaded.AttemptCount);

        var history = await store.ReadHistoryAsync(delivery.IdempotencyKey);
        Assert.Equal(
            [
                SchedulerPulseDeliveryStatus.Registered,
                SchedulerPulseDeliveryStatus.Fired,
                SchedulerPulseDeliveryStatus.Redelivered,
            ],
            history.Select(entry => entry.Status));
        Assert.Equal([1, 2, 3], history.Select(entry => entry.Sequence));
    }

    [Fact]
    public async Task Store_marks_terminal_states_with_structured_reasons()
    {
        await using var dataSource = fixture.CreateDataSource();
        await ResetSchedulerAsync(dataSource);
        await new PostgreSqlSchedulerPulseDeliveryMigrator(dataSource).MigrateAsync();
        var store = new PostgreSqlSchedulerPulseDeliveryStore(dataSource);
        var delivery = DeliveryRecord();
        await store.RecordFiredAsync(delivery);

        var delivered = await store.MarkDeliveredAsync(
            delivery.IdempotencyKey,
            new DateTimeOffset(2026, 7, 3, 17, 11, 0, TimeSpan.Zero));
        var skipped = await store.MarkSkippedAsync(
            delivery.IdempotencyKey,
            new DateTimeOffset(2026, 7, 3, 17, 12, 0, TimeSpan.Zero),
            new SchedulerPulseDeliveryReason("working-hours-closed", "The schedule window was outside working hours."));
        var failed = await store.MarkFailedAsync(
            delivery.IdempotencyKey,
            new DateTimeOffset(2026, 7, 3, 17, 13, 0, TimeSpan.Zero),
            new SchedulerPulseDeliveryReason("position-unavailable", "The target position could not be reached."));

        Assert.Equal(SchedulerPulseDeliveryStatus.Delivered, delivered.Status);
        Assert.Null(delivered.Reason);
        Assert.Equal(SchedulerPulseDeliveryStatus.Skipped, skipped.Status);
        Assert.Equal("working-hours-closed", skipped.Reason?.Code);
        Assert.Equal(SchedulerPulseDeliveryStatus.Failed, failed.Status);
        Assert.Equal("position-unavailable", failed.Reason?.Code);

        var history = await store.ReadHistoryAsync(delivery.IdempotencyKey);
        Assert.Equal(
            [
                SchedulerPulseDeliveryStatus.Registered,
                SchedulerPulseDeliveryStatus.Fired,
                SchedulerPulseDeliveryStatus.Delivered,
                SchedulerPulseDeliveryStatus.Skipped,
                SchedulerPulseDeliveryStatus.Failed,
            ],
            history.Select(entry => entry.Status));
    }

    private static async Task ResetSchedulerAsync(Npgsql.NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand("DROP SCHEMA IF EXISTS scheduler CASCADE;");
        await command.ExecuteNonQueryAsync();
    }

    private static SchedulerPulseDeliveryRecord DeliveryRecord()
    {
        var window = TemporalWindow.From(
            new DateTimeOffset(2026, 7, 3, 16, 55, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 6, 16, 55, 0, TimeSpan.Zero));
        var key = PulseIdempotencyKey.From(
            OrganizationId.From("acme-delivery"),
            PositionId.From("delivery-lead"),
            ScheduleId.From("daily-report"),
            window);

        return new SchedulerPulseDeliveryRecord(
            key,
            MessageId.From(Guid.Parse("11111111-1111-4111-8111-111111111111")),
            ThreadId.From(Guid.Parse("22222222-2222-4222-8222-222222222222")),
            new DateTimeOffset(2026, 7, 3, 17, 10, 0, TimeSpan.Zero));
    }
}
