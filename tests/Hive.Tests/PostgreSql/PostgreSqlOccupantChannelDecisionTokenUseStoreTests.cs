using Hive.Infrastructure.OccupantChannels.PostgreSql;
using Npgsql;

namespace Hive.Tests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlOccupantChannelDecisionTokenUseStoreTests(
    PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Migration_is_idempotent_and_consumption_is_atomic_across_store_instances()
    {
        await ResetAsync();
        await using (var dataSource = fixture.CreateDataSource())
        {
            var migrator = new PostgreSqlOccupantChannelTokenMigrator(dataSource);
            await migrator.MigrateAsync();
            await migrator.MigrateAsync();
        }

        var tokenId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var consumedAt = new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);
        var expiresAt = consumedAt.AddHours(1);
        await using var firstStore =
            new PostgreSqlOccupantChannelDecisionTokenUseStore(fixture.ConnectionString);

        var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(
            _ => firstStore.TryConsumeAsync(tokenId, expiresAt, consumedAt).AsTask()));

        Assert.Single(concurrent.Where(consumed => consumed));

        await using var restartedStore =
            new PostgreSqlOccupantChannelDecisionTokenUseStore(fixture.ConnectionString);
        Assert.False(await restartedStore.TryConsumeAsync(
            tokenId,
            expiresAt,
            consumedAt.AddMinutes(1)));
    }

    [Fact]
    public async Task Expired_entries_are_removed_without_accepting_an_expired_redemption()
    {
        await ResetAsync();
        await using var dataSource = fixture.CreateDataSource();
        await new PostgreSqlOccupantChannelTokenMigrator(dataSource).MigrateAsync();
        await using var store = new PostgreSqlOccupantChannelDecisionTokenUseStore(dataSource);
        var firstToken = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var secondToken = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var firstUse = new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);

        Assert.True(await store.TryConsumeAsync(
            firstToken,
            firstUse.AddMinutes(1),
            firstUse));
        Assert.True(await store.TryConsumeAsync(
            secondToken,
            firstUse.AddHours(2),
            firstUse.AddMinutes(2)));
        Assert.False(await store.TryConsumeAsync(
            Guid.NewGuid(),
            firstUse.AddMinutes(2),
            firstUse.AddMinutes(2)));

        await using var command = dataSource.CreateCommand(
            "SELECT token_id FROM occupant_channel.decision_token_uses ORDER BY token_id;");
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(secondToken, reader.GetGuid(0));
        Assert.False(await reader.ReadAsync());
    }

    private async Task ResetAsync()
    {
        await using var dataSource = fixture.CreateDataSource();
        await using var command = dataSource.CreateCommand(
            "DROP SCHEMA IF EXISTS occupant_channel CASCADE;");
        await command.ExecuteNonQueryAsync();
    }
}
