using Hive.Infrastructure.OccupantChannels;
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

        var operations = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        var concurrent = await Task.WhenAll(operations.Select(
            operation => firstStore.TryConsumeAsync(
                tokenId,
                operation,
                expiresAt,
                consumedAt).AsTask()));

        Assert.Single(concurrent.Where(
            result => result == OccupantChannelDecisionTokenUseResult.Consumed));
        Assert.Equal(
            7,
            concurrent.Count(result =>
                result == OccupantChannelDecisionTokenUseResult.AlreadyConsumed));
        var winningOperation = operations[Array.IndexOf(
            concurrent,
            OccupantChannelDecisionTokenUseResult.Consumed)];

        await using var restartedStore =
            new PostgreSqlOccupantChannelDecisionTokenUseStore(fixture.ConnectionString);
        Assert.Equal(
            OccupantChannelDecisionTokenUseResult.AlreadyConsumedByOperation,
            await restartedStore.TryConsumeAsync(
                tokenId,
                winningOperation,
                expiresAt,
                consumedAt.AddMinutes(1)));
        Assert.Equal(
            OccupantChannelDecisionTokenUseResult.AlreadyConsumed,
            await restartedStore.TryConsumeAsync(
            tokenId,
            Guid.NewGuid(),
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

        Assert.Equal(
            OccupantChannelDecisionTokenUseResult.Consumed,
            await store.TryConsumeAsync(
            firstToken,
            Guid.NewGuid(),
            firstUse.AddMinutes(1),
            firstUse));
        Assert.Equal(
            OccupantChannelDecisionTokenUseResult.Consumed,
            await store.TryConsumeAsync(
            secondToken,
            Guid.NewGuid(),
            firstUse.AddHours(2),
            firstUse.AddMinutes(2)));
        Assert.Equal(
            OccupantChannelDecisionTokenUseResult.AlreadyConsumed,
            await store.TryConsumeAsync(
            Guid.NewGuid(),
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
