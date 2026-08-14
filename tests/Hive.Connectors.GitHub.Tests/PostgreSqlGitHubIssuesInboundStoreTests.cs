using Hive.Connectors.GitHub.PostgreSql;
using Hive.Domain.Identity;
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

        Assert.Equal([1, 2, 3, 4], versions);
    }

    [Fact]
    public async Task Submitted_issue_and_comment_correlation_survives_restart_and_resolves_both_ways()
    {
        await ResetAndMigrateAsync();
        var issue = new GitHubIssuesInboundEvent(
            "issue:42",
            GitHubIssuesInboundEventKinds.Issue,
            "{\"number\":42,\"title\":\"Retry failed\"}");
        var comment = new GitHubIssuesInboundEvent(
            "comment:9001",
            GitHubIssuesInboundEventKinds.Comment,
            "{\"issue_number\":42,\"id\":9001,\"body\":\"Still failing\"}");
        var threadId = ThreadId.From(Guid.Parse("d615f47d-8c00-4df9-b493-c05537559e6a"));
        var rootDirectiveId = DirectiveId.From(
            Guid.Parse("18d00fd4-388f-421f-90b3-7d2484e212b3"));
        var commentDirectiveId = DirectiveId.From(
            Guid.Parse("6a710921-bfac-4a6f-b235-f7367c867e45"));
        var correlation = new GitHubIssueCorrelation(
            "acme-github",
            OrganizationId.From("acme"),
            "acme/payments",
            42,
            threadId,
            rootDirectiveId);

        await using (var first =
                     new PostgreSqlGitHubIssuesInboundStore(fixture.ConnectionString))
        {
            await first.CommitBatchAsync(
                expectedCheckpoint: null,
                Batch("cursor-1", issue, comment),
                CapturedAt,
                CapturedAt.AddMinutes(1));
            var pending = await first.ReadPendingAsync(
                "acme-github",
                "acme/payments",
                10);
            var issueEnvelope = Assert.Single(
                pending.Where(item => item.ExternalEventId == issue.ExternalEventId));
            var commentEnvelope = Assert.Single(
                pending.Where(item => item.ExternalEventId == comment.ExternalEventId));

            Assert.True(await first.TryCompleteAsync(
                issueEnvelope,
                Submitted(correlation, rootDirectiveId, CapturedAt.AddSeconds(1))));
            Assert.False(await first.TryCompleteAsync(
                issueEnvelope,
                Submitted(correlation, rootDirectiveId, CapturedAt.AddSeconds(1))));
            Assert.True(await first.TryCompleteAsync(
                commentEnvelope,
                Submitted(correlation, commentDirectiveId, CapturedAt.AddSeconds(2))));
        }

        await using var restarted =
            new PostgreSqlGitHubIssuesInboundStore(fixture.ConnectionString);
        var byIssue = await restarted.FindCorrelationByIssueAsync(
            "acme-github",
            OrganizationId.From("acme"),
            "ACME/PAYMENTS",
            42);
        var byThread = await restarted.FindCorrelationByThreadAsync(
            "acme-github",
            OrganizationId.From("acme"),
            threadId);
        var byRootDirective = await restarted.FindCorrelationByDirectiveAsync(
            "acme-github",
            OrganizationId.From("acme"),
            rootDirectiveId);
        var byCommentDirective = await restarted.FindCorrelationByDirectiveAsync(
            "acme-github",
            OrganizationId.From("acme"),
            commentDirectiveId);

        Assert.Equal(correlation, byIssue);
        Assert.Equal(correlation, byThread);
        Assert.Equal(correlation, byRootDirective);
        Assert.Equal(correlation, byCommentDirective);
        Assert.Null(await restarted.FindCorrelationByThreadAsync(
            "other-instance",
            OrganizationId.From("acme"),
            threadId));
        Assert.Empty(await restarted.ReadPendingAsync(
            "acme-github",
            "acme/payments",
            10));
    }

    [Fact]
    public async Task Divergent_issue_correlation_rolls_back_and_leaves_event_pending()
    {
        await ResetAndMigrateAsync();
        await using var store =
            new PostgreSqlGitHubIssuesInboundStore(fixture.ConnectionString);
        await store.CommitBatchAsync(
            expectedCheckpoint: null,
            Batch(
                "cursor-1",
                Event("issue:42", GitHubIssuesInboundEventKinds.Issue, 42),
                Event("comment:9001", GitHubIssuesInboundEventKinds.Comment, 9001)),
            CapturedAt,
            CapturedAt.AddMinutes(1));
        var pending = await store.ReadPendingAsync(
            "acme-github",
            "acme/payments",
            10);
        var correlation = new GitHubIssueCorrelation(
            "acme-github",
            OrganizationId.From("acme"),
            "acme/payments",
            42,
            ThreadId.From(Guid.Parse("d615f47d-8c00-4df9-b493-c05537559e6a")),
            DirectiveId.From(Guid.Parse("18d00fd4-388f-421f-90b3-7d2484e212b3")));
        var issueEnvelope = Assert.Single(
            pending.Where(item => item.ExternalEventId == "issue:42"));
        var commentEnvelope = Assert.Single(
            pending.Where(item => item.ExternalEventId == "comment:9001"));
        Assert.True(await store.TryCompleteAsync(
            issueEnvelope,
            Submitted(correlation, correlation.RootDirectiveId, CapturedAt.AddSeconds(1))));
        var divergent = new GitHubIssueCorrelation(
            correlation.InstanceId,
            correlation.OrganizationId,
            correlation.Repository,
            correlation.IssueNumber,
            ThreadId.From(Guid.Parse("4340bed5-f30d-47dc-8620-4852739c63d2")),
            correlation.RootDirectiveId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.TryCompleteAsync(
            commentEnvelope,
            Submitted(
                divergent,
                DirectiveId.From(Guid.Parse("6a710921-bfac-4a6f-b235-f7367c867e45")),
                CapturedAt.AddSeconds(2))));

        Assert.Equal(
            correlation,
            await store.FindCorrelationByIssueAsync(
                "acme-github",
                OrganizationId.From("acme"),
                "acme/payments",
                42));
        Assert.Equal(
            ["comment:9001"],
            (await store.ReadPendingAsync(
                "acme-github",
                "acme/payments",
                10)).Select(item => item.ExternalEventId));
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

    private static GitHubIssuesInboundCompletion Submitted(
        GitHubIssueCorrelation correlation,
        DirectiveId directiveId,
        DateTimeOffset completedAtUtc) =>
        new(
            GitHubIssuesInboundCompletionState.Submitted,
            completedAtUtc,
            submission: new GitHubIssueSubmissionCorrelation(correlation, directiveId));
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
