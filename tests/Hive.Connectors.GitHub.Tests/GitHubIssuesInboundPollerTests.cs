using Hive.Domain.Identity;
using Microsoft.Extensions.Options;

namespace Hive.Connectors.GitHub.Tests;

public sealed class GitHubIssuesInboundPollerTests
{
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Cycle_polls_instances_and_repositories_sequentially_with_no_overlap()
    {
        var catalog = Catalog(
            Instance("zeta", ["acme/zeta"]),
            Instance("alpha", ["acme/two", "acme/one"]));
        var client = new RecordingClient(async call =>
        {
            await Task.Delay(20);
            return Batch(call, $"cursor-{call.Repository}",
                new GitHubIssuesInboundEvent(
                    $"issue:{call.Repository}:1",
                    GitHubIssuesInboundEventKinds.Issue,
                    "{\"number\":1}"));
        });
        var store = new RecordingStore();
        var poller = new GitHubIssuesInboundPoller(
            catalog,
            client,
            store,
            new ManualTimeProvider(ObservedAt));

        var result = await poller.PollDueRepositoriesAsync();

        Assert.Equal(
            ["alpha/acme/one", "alpha/acme/two", "zeta/acme/zeta"],
            client.Calls.Select(call => $"{call.Instance.InstanceId}/{call.Repository}"));
        Assert.Equal(1, client.MaximumConcurrency);
        Assert.All(result.Repositories, item =>
            Assert.Equal(GitHubIssuesRepositoryPollStatus.Committed, item.Status));
        Assert.All(store.Commits, commit =>
            Assert.Equal(ObservedAt.AddSeconds(30), commit.NextPollAtUtc));
    }

    [Fact]
    public async Task Future_checkpoint_defers_fetch_and_persisted_rate_limit_wins_over_interval()
    {
        var instance = Instance("alpha", ["acme/one"]);
        var store = new RecordingStore
        {
            Checkpoint = new GitHubIssuesPollingCheckpoint(
                instance.InstanceId,
                "acme/one",
                "cursor-1",
                ObservedAt.AddMinutes(1)),
        };
        var client = new RecordingClient(call => Task.FromResult(
            Batch(call, "cursor-2", rateLimitNotBeforeUtc: ObservedAt.AddMinutes(5))));
        var time = new ManualTimeProvider(ObservedAt);
        var poller = new GitHubIssuesInboundPoller(
            Catalog(instance),
            client,
            store,
            time);

        var deferred = Assert.Single((await poller.PollDueRepositoriesAsync()).Repositories);

        Assert.Equal(GitHubIssuesRepositoryPollStatus.Deferred, deferred.Status);
        Assert.Equal(ObservedAt.AddMinutes(1), deferred.NextPollAtUtc);
        Assert.Empty(client.Calls);
        Assert.Empty(store.Commits);

        time.UtcNow = ObservedAt.AddMinutes(1);
        var committed = Assert.Single((await poller.PollDueRepositoriesAsync()).Repositories);

        Assert.Equal(GitHubIssuesRepositoryPollStatus.Committed, committed.Status);
        Assert.Equal("cursor-1", Assert.Single(client.Calls).Cursor);
        Assert.Equal(ObservedAt.AddMinutes(5), committed.NextPollAtUtc);
        Assert.Equal(ObservedAt.AddMinutes(5), Assert.Single(store.Commits).NextPollAtUtc);
    }

    [Fact]
    public async Task Source_failure_does_not_commit_or_stop_later_repositories_and_exposes_closed_error()
    {
        var client = new RecordingClient(call =>
            string.Equals(call.Repository, "acme/one", StringComparison.Ordinal)
                ? Task.FromException<GitHubIssuesInboundBatch>(
                    new InvalidOperationException("secret-bearing diagnostic"))
                : Task.FromResult(Batch(call, "cursor-2")));
        var store = new RecordingStore();
        var poller = new GitHubIssuesInboundPoller(
            Catalog(Instance("alpha", ["acme/one", "acme/two"])),
            client,
            store,
            new ManualTimeProvider(ObservedAt));

        var result = await poller.PollDueRepositoriesAsync();

        Assert.Equal(2, result.Repositories.Length);
        Assert.Equal(GitHubIssuesRepositoryPollStatus.Failed, result.Repositories[0].Status);
        Assert.Equal(GitHubIssuesInboundPoller.PollFailedCode, result.Repositories[0].ErrorCode);
        Assert.Equal(GitHubIssuesRepositoryPollStatus.Committed, result.Repositories[1].Status);
        Assert.Single(store.Commits);
        Assert.Equal("acme/two", store.Commits[0].Batch.Repository);
        Assert.DoesNotContain(
            result.Repositories,
            item => item.ErrorCode?.Contains("secret", StringComparison.OrdinalIgnoreCase) is true);
    }

    [Fact]
    public async Task Optimistic_checkpoint_conflict_returns_safe_refetch_without_claiming_inserts()
    {
        var client = new RecordingClient(call => Task.FromResult(Batch(
            call,
            "cursor-2",
            new GitHubIssuesInboundEvent(
                "issue:1",
                GitHubIssuesInboundEventKinds.Issue,
                "{\"number\":1}"))));
        var store = new RecordingStore { ApplyCommit = false };
        var poller = new GitHubIssuesInboundPoller(
            Catalog(Instance("alpha", ["acme/one"])),
            client,
            store,
            new ManualTimeProvider(ObservedAt));

        var result = Assert.Single((await poller.PollDueRepositoriesAsync()).Repositories);

        Assert.Equal(GitHubIssuesRepositoryPollStatus.ConcurrentCheckpoint, result.Status);
        Assert.Equal(1, result.FetchedCount);
        Assert.Equal(0, result.InsertedCount);
    }

    [Fact]
    public void Inbound_transport_contract_rejects_duplicate_ids_invalid_json_and_non_utc_rate_limit()
    {
        Assert.Throws<ArgumentException>(() => new GitHubIssuesInboundEvent(
            "issue:1",
            GitHubIssuesInboundEventKinds.Issue,
            "not-json"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GitHubIssuesInboundEvent(
            "issue:1",
            "pull-request",
            "{}"));
        Assert.Throws<ArgumentException>(() => new GitHubIssuesInboundBatch(
            "alpha",
            "acme/one",
            "cursor",
            [
                new GitHubIssuesInboundEvent("same", GitHubIssuesInboundEventKinds.Issue, "{}"),
                new GitHubIssuesInboundEvent("same", GitHubIssuesInboundEventKinds.Comment, "{}"),
            ]));
        Assert.Throws<ArgumentException>(() => new GitHubIssuesInboundBatch(
            "alpha",
            "acme/one",
            "cursor",
            [],
            new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.FromHours(1))));
    }

    private static GitHubIssuesInboundBatch Batch(
        ClientCall call,
        string nextCursor,
        params GitHubIssuesInboundEvent[] events) =>
        new(
            call.Instance.InstanceId,
            call.Repository,
            nextCursor,
            events);

    private static GitHubIssuesInboundBatch Batch(
        ClientCall call,
        string nextCursor,
        DateTimeOffset rateLimitNotBeforeUtc) =>
        new(
            call.Instance.InstanceId,
            call.Repository,
            nextCursor,
            [],
            rateLimitNotBeforeUtc);

    private static GitHubIssuesConnectorInstanceConfiguration Instance(
        string instanceId,
        IReadOnlyList<string> repositories) =>
        new(
            instanceId,
            OrganizationId.From("acme"),
            repositories,
            PositionId.From("bug-triage"),
            [],
            new GitHubIssuesPollingConfiguration(TimeSpan.FromSeconds(30), 100));

    private static GitHubIssuesConnectorConfigurationCatalog Catalog(
        params GitHubIssuesConnectorInstanceConfiguration[] instances)
    {
        var raw = instances.Select(instance => new GitHubIssuesConnectorInstanceOptions
        {
            InstanceId = instance.InstanceId,
            OrganizationId = instance.OrganizationId.Value,
            Repositories = instance.Repositories.ToArray(),
            InboundDirectiveTarget = instance.InboundDirectiveTarget.Value,
            OutboundOperations = instance.OutboundOperations.ToArray(),
            Polling = new GitHubIssuesPollingOptions
            {
                Interval = System.Xml.XmlConvert.ToString(instance.Polling.Interval),
                PageSize = instance.Polling.PageSize,
            },
        }).ToArray();
        return new GitHubIssuesConnectorConfigurationCatalog(Options.Create(
            new GitHubIssuesConnectorOptions
            {
                Instances = raw,
                Credentials = instances.Select(instance =>
                    new GitHubIssuesConnectorCredentialOptions
                    {
                        InstanceId = instance.InstanceId,
                        Token = "test-token",
                    }).ToArray(),
            }));
    }

    private sealed record ClientCall(
        GitHubIssuesConnectorInstanceConfiguration Instance,
        string Repository,
        string? Cursor,
        int PageSize);

    private sealed class RecordingClient(
        Func<ClientCall, Task<GitHubIssuesInboundBatch>> fetch)
        : IGitHubIssuesInboundClient
    {
        private int _concurrency;
        private int _maximumConcurrency;

        public List<ClientCall> Calls { get; } = [];

        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public async Task<GitHubIssuesInboundBatch> FetchBatchAsync(
            GitHubIssuesConnectorInstanceConfiguration instance,
            string repository,
            string? cursor,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            var call = new ClientCall(instance, repository, cursor, pageSize);
            Calls.Add(call);
            var current = Interlocked.Increment(ref _concurrency);
            UpdateMaximum(current);
            try
            {
                return await fetch(call);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrency);
            }
        }

        private void UpdateMaximum(int current)
        {
            var observed = Volatile.Read(ref _maximumConcurrency);
            while (current > observed)
            {
                var previous = Interlocked.CompareExchange(
                    ref _maximumConcurrency,
                    current,
                    observed);
                if (previous == observed)
                {
                    return;
                }

                observed = previous;
            }
        }
    }

    private sealed class RecordingStore : IGitHubIssuesInboundStore
    {
        private readonly Dictionary<string, GitHubIssuesPollingCheckpoint> _checkpoints =
            new(StringComparer.OrdinalIgnoreCase);

        public GitHubIssuesPollingCheckpoint? Checkpoint
        {
            get => _checkpoints.Values.SingleOrDefault();
            set
            {
                _checkpoints.Clear();
                if (value is not null)
                {
                    _checkpoints[Key(value.InstanceId, value.Repository)] = value;
                }
            }
        }

        public bool ApplyCommit { get; init; } = true;

        public List<CommitCall> Commits { get; } = [];

        public ValueTask<GitHubIssuesPollingCheckpoint?> ReadCheckpointAsync(
            string instanceId,
            string repository,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                _checkpoints.GetValueOrDefault(Key(instanceId, repository)));

        public Task<GitHubIssuesInboundCommitResult> CommitBatchAsync(
            GitHubIssuesPollingCheckpoint? expectedCheckpoint,
            GitHubIssuesInboundBatch batch,
            DateTimeOffset capturedAtUtc,
            DateTimeOffset nextPollAtUtc,
            CancellationToken cancellationToken = default)
        {
            Commits.Add(new CommitCall(
                expectedCheckpoint,
                batch,
                capturedAtUtc,
                nextPollAtUtc));
            if (!ApplyCommit)
            {
                return Task.FromResult(GitHubIssuesInboundCommitResult.ConcurrentCheckpoint());
            }

            var checkpoint = new GitHubIssuesPollingCheckpoint(
                batch.InstanceId,
                batch.Repository,
                batch.NextCursor,
                nextPollAtUtc);
            _checkpoints[Key(batch.InstanceId, batch.Repository)] = checkpoint;
            return Task.FromResult(new GitHubIssuesInboundCommitResult(
                true,
                batch.Events.Length,
                checkpoint));
        }

        public Task<IReadOnlyList<GitHubIssuesInboundEnvelope>> ReadPendingAsync(
            string instanceId,
            string repository,
            int limit,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> TryCompleteAsync(
            GitHubIssuesInboundEnvelope envelope,
            GitHubIssuesInboundCompletion completion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private static string Key(string instanceId, string repository) =>
            $"{instanceId}/{repository}";
    }

    private sealed record CommitCall(
        GitHubIssuesPollingCheckpoint? ExpectedCheckpoint,
        GitHubIssuesInboundBatch Batch,
        DateTimeOffset CapturedAtUtc,
        DateTimeOffset NextPollAtUtc);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
