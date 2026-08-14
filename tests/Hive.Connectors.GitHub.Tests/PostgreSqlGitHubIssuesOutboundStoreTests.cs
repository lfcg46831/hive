using Hive.Connectors.GitHub.PostgreSql;
using Hive.Domain.Identity;

namespace Hive.Connectors.GitHub.Tests;

[Collection(GitHubPostgreSqlCollection.Name)]
public sealed class PostgreSqlGitHubIssuesOutboundStoreTests(
    GitHubPostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset At =
        new(2026, 8, 14, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Success_survives_restart_and_divergent_payload_fails_closed()
    {
        await ResetAndMigrateAsync();
        var descriptor = Descriptor(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        await using (var first =
                     new PostgreSqlGitHubIssuesOutboundStore(fixture.ConnectionString))
        await using (var lease = await first.AcquireAsync(descriptor))
        {
            Assert.Equal(GitHubIssuesOutboundOperationState.Pending, lease.Snapshot.State);
            await lease.RecordAttemptAsync("github-outbound-published", At.AddSeconds(1));
            await lease.CompleteSuccessAsync("receipt-42", At.AddSeconds(2));
        }

        await using var restarted =
            new PostgreSqlGitHubIssuesOutboundStore(fixture.ConnectionString);
        await using (var replay = await restarted.AcquireAsync(descriptor))
        {
            Assert.Equal(GitHubIssuesOutboundOperationState.Succeeded, replay.Snapshot.State);
            Assert.Equal(1, replay.Snapshot.AttemptCount);
            Assert.Equal("receipt-42", replay.Snapshot.Receipt);
        }

        await Assert.ThrowsAsync<GitHubIssuesOutboundOperationConflictException>(async () =>
        {
            await using var ignored = await restarted.AcquireAsync(Descriptor(
                descriptor.OperationKey,
                "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"));
        });
    }

    private async Task ResetAndMigrateAsync()
    {
        await using var dataSource = fixture.CreateDataSource();
        await using (var reset = dataSource.CreateCommand(
                         "DROP SCHEMA IF EXISTS github_connector CASCADE;"))
        {
            await reset.ExecuteNonQueryAsync();
        }

        await new PostgreSqlGitHubIssuesInboundMigrator(dataSource).MigrateAsync();
    }

    private static GitHubIssuesOutboundOperationDescriptor Descriptor(
        string operationKey,
        string payloadHash) =>
        new(
            operationKey,
            payloadHash,
            new GitHubIssueCorrelation(
                "acme-github",
                OrganizationId.From("acme"),
                "acme/payments",
                42,
                ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111111")),
                DirectiveId.From(Guid.Parse("33333333-3333-3333-3333-333333333333"))),
            PositionId.From("bug-triage"),
            DirectiveId.From(Guid.Parse("33333333-3333-3333-3333-333333333333")),
            GitHubIssuesOutboundOperations.Comment,
            At);
}

