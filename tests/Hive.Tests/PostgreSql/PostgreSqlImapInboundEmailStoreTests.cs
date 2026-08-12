using System.Text;
using Hive.Infrastructure.OccupantChannels;
using Hive.Infrastructure.OccupantChannels.PostgreSql;

namespace Hive.Tests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlImapInboundEmailStoreTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Batch_checkpoint_and_envelopes_survive_restart_without_duplicates()
    {
        await ResetAndMigrateAsync();
        var capturedAt = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        var firstBatch = Batch(7, 12, (11, "first"), (12, "second"));

        await using (var firstStore =
                     new PostgreSqlImapInboundEmailStore(fixture.ConnectionString))
        {
            var committed = await firstStore.CommitBatchAsync(null, firstBatch, capturedAt);
            Assert.True(committed.IsApplied);
            Assert.Equal(2, committed.InsertedCount);
        }

        await using var restarted =
            new PostgreSqlImapInboundEmailStore(fixture.ConnectionString);
        var checkpoint = await restarted.ReadCheckpointAsync("occupant-replies", "INBOX");
        Assert.Equal(new ImapInboundEmailCheckpoint(
            "occupant-replies",
            "INBOX",
            7,
            12), checkpoint);
        var pending = await restarted.ReadPendingAsync("occupant-replies", "INBOX", 10);
        Assert.Equal([11U, 12U], pending.Select(item => item.Uid));
        Assert.Equal(["first", "second"], pending.Select(item => Encoding.ASCII.GetString(item.RawMessage)));

        var staleRedelivery = await restarted.CommitBatchAsync(null, firstBatch, capturedAt.AddMinutes(1));
        Assert.False(staleRedelivery.IsApplied);
        Assert.Equal(2, (await restarted.ReadPendingAsync("occupant-replies", "INBOX", 10)).Count);
    }

    [Fact]
    public async Task Concurrent_failover_batches_use_compare_and_swap_and_insert_once()
    {
        await ResetAndMigrateAsync();
        var capturedAt = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        await using var first = new PostgreSqlImapInboundEmailStore(fixture.ConnectionString);
        await using var second = new PostgreSqlImapInboundEmailStore(fixture.ConnectionString);
        var initial = await first.CommitBatchAsync(null, Batch(7, 10), capturedAt);
        var expected = initial.Checkpoint!;
        var competingBatch = Batch(7, 11, (11, "once"));

        var outcomes = await Task.WhenAll(
            first.CommitBatchAsync(expected, competingBatch, capturedAt.AddSeconds(1)),
            second.CommitBatchAsync(expected, competingBatch, capturedAt.AddSeconds(1)));

        Assert.Single(outcomes.Where(outcome => outcome.IsApplied));
        Assert.Single(outcomes.Where(outcome => !outcome.IsApplied));
        var pending = await first.ReadPendingAsync("occupant-replies", "INBOX", 10);
        Assert.Single(pending);
        Assert.Equal(11U, pending[0].Uid);
        Assert.Equal(11U, (await first.ReadCheckpointAsync("occupant-replies", "INBOX"))!.LastUid);
    }

    [Fact]
    public async Task Uidvalidity_reset_opens_a_new_generation_without_overwriting_history()
    {
        await ResetAndMigrateAsync();
        var capturedAt = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        await using var store = new PostgreSqlImapInboundEmailStore(fixture.ConnectionString);
        var first = await store.CommitBatchAsync(
            null,
            Batch(7, 900, (900, "old-generation")),
            capturedAt);

        var reset = await store.CommitBatchAsync(
            first.Checkpoint,
            Batch(8, 1, (1, "new-generation")),
            capturedAt.AddMinutes(1));

        Assert.True(reset.IsApplied);
        Assert.Equal(8U, reset.Checkpoint!.UidValidity);
        Assert.Equal(1U, reset.Checkpoint.LastUid);
        var pending = await store.ReadPendingAsync("occupant-replies", "INBOX", 10);
        Assert.Equal(2, pending.Count);
        Assert.Contains(pending, item => item.UidValidity == 7 && item.Uid == 900);
        Assert.Contains(pending, item => item.UidValidity == 8 && item.Uid == 1);
    }

    [Fact]
    public async Task Migration_is_idempotent()
    {
        await ResetAsync();
        await using var dataSource = fixture.CreateDataSource();
        var migrator = new PostgreSqlOccupantChannelTokenMigrator(dataSource);

        await migrator.MigrateAsync();
        await migrator.MigrateAsync();

        await using var command = dataSource.CreateCommand(
            "SELECT version FROM occupant_channel.schema_migrations ORDER BY version;");
        await using var reader = await command.ExecuteReaderAsync();
        var versions = new List<int>();
        while (await reader.ReadAsync())
        {
            versions.Add(reader.GetInt32(0));
        }

        Assert.Equal([1, 2], versions);
    }

    private async Task ResetAndMigrateAsync()
    {
        await ResetAsync();
        await using var dataSource = fixture.CreateDataSource();
        await new PostgreSqlOccupantChannelTokenMigrator(dataSource).MigrateAsync();
    }

    private async Task ResetAsync()
    {
        await using var dataSource = fixture.CreateDataSource();
        await using var command = dataSource.CreateCommand(
            "DROP SCHEMA IF EXISTS occupant_channel CASCADE;");
        await command.ExecuteNonQueryAsync();
    }

    private static ImapInboundEmailBatch Batch(
        uint uidValidity,
        uint highestUid,
        params (uint Uid, string Body)[] messages) =>
        new(
            "occupant-replies",
            "INBOX",
            uidValidity,
            highestUid,
            messages.Select(message => new FetchedImapMessage(
                message.Uid,
                Encoding.ASCII.GetBytes(message.Body))).ToArray());
}
