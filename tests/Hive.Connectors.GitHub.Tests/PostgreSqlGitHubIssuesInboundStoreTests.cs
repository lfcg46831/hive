using Hive.Connectors.GitHub.PostgreSql;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Hive.Connectors.GitHub.Tests;

[Collection(GitHubPostgreSqlCollection.Name)]
public sealed class PostgreSqlGitHubIssuesInboundStoreTests(
    GitHubPostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset CapturedAt =
        new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Checkpoint_events_and_rate_limit_survive_restart_without_duplicates()
    {
        await ResetAndMigrateAsync();
        var batch = Batch(
            "cursor-2",
            Event("issue:1", GitHubIssuesInboundEventKinds.Issue, 1),
            Event("comment:20", GitHubIssuesInboundEventKinds.Comment, 20));
        var notBefore = CapturedAt.AddMinutes(5);

        await using (var first =
                     new PostgreSqlGitHubIssuesInboundStore(fixture.ConnectionString))
        {
            var committed = await first.CommitBatchAsync(
                null,
                batch,
                CapturedAt,
                notBefore);
            Assert.True(committed.IsApplied);
            Assert.Equal(2, committed.InsertedCount);
        }

        await using var restarted =
            new PostgreSqlGitHubIssuesInboundStore(fixture.ConnectionString);
        var checkpoint = await restarted.ReadCheckpointAsync(
            "acme-github",
            "acme/payments");
        Assert.Equal(new GitHubIssuesPollingCheckpoint(
            "acme-github",
            "acme/payments",
            "cursor-2",
            notBefore), checkpoint);
        var pending = await restarted.ReadPendingAsync(
            "acme-github",
            "acme/payments",
            10);
        Assert.Equal(["comment:20", "issue:1"], pending.Select(item => item.ExternalEventId));

        var replay = await restarted.CommitBatchAsync(
            checkpoint,
            Batch("cursor-3", batch.Events.ToArray()),
            CapturedAt.AddMinutes(5),
            CapturedAt.AddMinutes(6));
        Assert.True(replay.IsApplied);
        Assert.Equal(0, replay.InsertedCount);
        Assert.Equal(
            2,
            (await restarted.ReadPendingAsync(
                "acme-github",
                "acme/payments",
                10)).Count);
    }

    [Fact]
    public async Task Concurrent_failover_commits_compare_checkpoint_and_insert_once()
    {
        await ResetAndMigrateAsync();
        await using var first =
            new PostgreSqlGitHubIssuesInboundStore(fixture.ConnectionString);
        await using var second =
            new PostgreSqlGitHubIssuesInboundStore(fixture.ConnectionString);
        var initial = await first.CommitBatchAsync(
            null,
            Batch("cursor-1"),
            CapturedAt,
            CapturedAt.AddSeconds(1));
        var expected = initial.Checkpoint!;
        var competing = Batch(
            "cursor-2",
            Event("issue:1", GitHubIssuesInboundEventKinds.Issue, 1));

        var outcomes = await Task.WhenAll(
            first.CommitBatchAsync(
                expected,
                competing,
                CapturedAt.AddSeconds(1),
                CapturedAt.AddSeconds(2)),
            second.CommitBatchAsync(
                expected,
                competing,
                CapturedAt.AddSeconds(1),
                CapturedAt.AddSeconds(2)));

        Assert.Single(outcomes.Where(outcome => outcome.IsApplied));
        Assert.Single(outcomes.Where(outcome => !outcome.IsApplied));
        Assert.Single(await first.ReadPendingAsync("acme-github", "acme/payments", 10));
        Assert.Equal(
            "cursor-2",
            (await first.ReadCheckpointAsync("acme-github", "acme/payments"))!.Cursor);
    }

    [Fact]
    public async Task Migration_is_idempotent()
    {
        await ResetAsync();
        await using var dataSource = fixture.CreateDataSource();
        var migrator = new PostgreSqlGitHubIssuesInboundMigrator(dataSource);

        await migrator.MigrateAsync();
        await migrator.MigrateAsync();

        await using var command = dataSource.CreateCommand(
            "SELECT version FROM github_connector.schema_migrations ORDER BY version;");
        await using var reader = await command.ExecuteReaderAsync();
        var versions = new List<int>();
        while (await reader.ReadAsync())
        {
            versions.Add(reader.GetInt32(0));
        }

        Assert.Equal([1, 2], versions);
    }

    [Fact]
    public async Task Pending_event_completes_once_with_closed_result_metadata()
    {
        await ResetAndMigrateAsync();
        await using var store =
            new PostgreSqlGitHubIssuesInboundStore(fixture.ConnectionString);
        await store.CommitBatchAsync(
            expectedCheckpoint: null,
            Batch(
                "cursor-1",
                Event("comment:20", GitHubIssuesInboundEventKinds.Comment, 20)),
            CapturedAt,
            CapturedAt.AddMinutes(1));
        var envelope = Assert.Single(await store.ReadPendingAsync(
            "acme-github",
            "acme/payments",
            10));
        var completion = new GitHubIssuesInboundCompletion(
            GitHubIssuesInboundCompletionState.Rejected,
            CapturedAt.AddSeconds(1),
            GitHubIssuesInboundProcessingReasonCodes.PayloadInvalid);

        Assert.True(await store.TryCompleteAsync(envelope, completion));
        Assert.False(await store.TryCompleteAsync(envelope, completion));
        Assert.Empty(await store.ReadPendingAsync("acme-github", "acme/payments", 10));

        await using var dataSource = fixture.CreateDataSource();
        await using var command = dataSource.CreateCommand(
            """
            SELECT processing_state, processed_at, rejection_code
            FROM github_connector.inbound_events
            WHERE instance_id = 'acme-github'
              AND repository = 'acme/payments'
              AND external_event_id = 'comment:20';
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("rejected", reader.GetString(0));
        Assert.Equal(CapturedAt.AddSeconds(1), reader.GetFieldValue<DateTimeOffset>(1));
        Assert.Equal(
            GitHubIssuesInboundProcessingReasonCodes.PayloadInvalid,
            reader.GetString(2));
        Assert.False(await reader.ReadAsync());
    }

    private async Task ResetAndMigrateAsync()
    {
        await ResetAsync();
        await using var dataSource = fixture.CreateDataSource();
        await new PostgreSqlGitHubIssuesInboundMigrator(dataSource).MigrateAsync();
    }

    private async Task ResetAsync()
    {
        await using var dataSource = fixture.CreateDataSource();
        await using var command = dataSource.CreateCommand(
            "DROP SCHEMA IF EXISTS github_connector CASCADE;");
        await command.ExecuteNonQueryAsync();
    }

    private static GitHubIssuesInboundBatch Batch(
        string cursor,
        params GitHubIssuesInboundEvent[] events) =>
        new("acme-github", "acme/payments", cursor, events);

    private static GitHubIssuesInboundEvent Event(
        string id,
        string kind,
        int value) =>
        new(id, kind, $"{{\"value\":{value}}}");
}

[CollectionDefinition(Name)]
public sealed class GitHubPostgreSqlCollection
    : ICollectionFixture<GitHubPostgreSqlFixture>
{
    public const string Name = "GitHub connector PostgreSQL";
}

public sealed class GitHubPostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public NpgsqlDataSource CreateDataSource() =>
        NpgsqlDataSource.Create(ConnectionString);

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
