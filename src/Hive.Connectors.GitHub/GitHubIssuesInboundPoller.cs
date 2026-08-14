using Hive.Domain.Auditing;
using Hive.Domain.Identity;

namespace Hive.Connectors.GitHub;

internal sealed class GitHubIssuesInboundPoller(
    GitHubIssuesConnectorConfigurationCatalog catalog,
    IGitHubIssuesInboundClient client,
    IGitHubIssuesInboundStore store,
    IJourneyAuditLog auditLog,
    TimeProvider timeProvider) : IGitHubIssuesInboundPoller
{
    internal const string PollFailedCode = "github-issues-poll-failed";

    public async Task<GitHubIssuesPollingCycleResult> PollDueRepositoriesAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<GitHubIssuesRepositoryPollResult>();
        foreach (var instance in catalog.Instances
                     .OrderBy(value => value.InstanceId, StringComparer.Ordinal))
        {
            foreach (var repository in instance.Repositories
                         .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(await PollRepositoryAsync(
                        instance,
                        repository,
                        cancellationToken)
                    .ConfigureAwait(false));
            }
        }

        return new GitHubIssuesPollingCycleResult(results);
    }

    internal async Task<GitHubIssuesRepositoryPollResult> PollRepositoryAsync(
        GitHubIssuesConnectorInstanceConfiguration instance,
        string repository,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var observedAtUtc = timeProvider.GetUtcNow();
        var fallbackNextPollAtUtc = SafeAdd(observedAtUtc, instance.Polling.Interval);
        var requestedScope = GitHubIssuesScopePolicy.AuthorizeInbound(instance, repository);
        if (!requestedScope.IsAllowed)
        {
            return IgnoreOutOfScope(
                instance,
                repository,
                requestedScope.DeniedDimension!,
                observedAtUtc,
                fallbackNextPollAtUtc);
        }

        try
        {
            var checkpoint = await store
                .ReadCheckpointAsync(instance.InstanceId, repository, cancellationToken)
                .ConfigureAwait(false);
            if (checkpoint is not null && checkpoint.NotBeforeUtc > observedAtUtc)
            {
                return new GitHubIssuesRepositoryPollResult(
                    instance.InstanceId,
                    repository,
                    GitHubIssuesRepositoryPollStatus.Deferred,
                    0,
                    0,
                    checkpoint.NotBeforeUtc);
            }

            var batch = await client
                .FetchBatchAsync(
                    instance,
                    repository,
                    checkpoint?.Cursor,
                    instance.Polling.PageSize,
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateBatchInstance(instance, batch);
            var returnedScope = GitHubIssuesScopePolicy.AuthorizeInbound(
                instance,
                batch.Repository);
            if (!returnedScope.IsAllowed)
            {
                return IgnoreOutOfScope(
                    instance,
                    batch.Repository,
                    returnedScope.DeniedDimension!,
                    observedAtUtc,
                    fallbackNextPollAtUtc);
            }

            if (!string.Equals(batch.Repository, repository, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The GitHub Issues client returned a batch for a different in-scope repository.");
            }

            var nextPollAtUtc = batch.RateLimitNotBeforeUtc is { } rateLimit
                && rateLimit > fallbackNextPollAtUtc
                    ? rateLimit
                    : fallbackNextPollAtUtc;
            var committed = await store
                .CommitBatchAsync(
                    checkpoint,
                    batch,
                    observedAtUtc,
                    nextPollAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);

            return new GitHubIssuesRepositoryPollResult(
                instance.InstanceId,
                repository,
                committed.IsApplied
                    ? GitHubIssuesRepositoryPollStatus.Committed
                    : GitHubIssuesRepositoryPollStatus.ConcurrentCheckpoint,
                batch.Events.Length,
                committed.InsertedCount,
                committed.Checkpoint?.NotBeforeUtc ?? fallbackNextPollAtUtc);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // The adapter never forwards exception messages because a future HTTP exception may
            // contain an endpoint, response body or credential-derived diagnostic. T08 supplies
            // structured transport errors without weakening this log boundary.
            return new GitHubIssuesRepositoryPollResult(
                instance.InstanceId,
                repository,
                GitHubIssuesRepositoryPollStatus.Failed,
                0,
                0,
                fallbackNextPollAtUtc,
                PollFailedCode);
        }
    }

    private static void ValidateBatchInstance(
        GitHubIssuesConnectorInstanceConfiguration instance,
        GitHubIssuesInboundBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (!string.Equals(batch.InstanceId, instance.InstanceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The GitHub Issues client returned a batch for a different configured source.");
        }
    }

    private GitHubIssuesRepositoryPollResult IgnoreOutOfScope(
        GitHubIssuesConnectorInstanceConfiguration instance,
        string repository,
        string deniedDimension,
        DateTimeOffset observedAtUtc,
        DateTimeOffset nextPollAtUtc)
    {
        var canonicalRepository = GitHubIssuesScopePolicy.CanonicalRepository(repository);
        var identity = string.Join(
            "\n",
            "hive:github-issues:scope-audit:v1",
            "inbound",
            instance.OrganizationId.Value,
            instance.InstanceId,
            canonicalRepository);
        var threadId = ThreadId.From(DeterministicGuid.FromName(identity + "\nthread"));
        var messageId = MessageId.From(DeterministicGuid.FromName(identity + "\nmessage"));
        auditLog.Append(JourneyAuditRecord.Create(
            JourneyAuditStage.ConnectorInbound,
            JourneyAuditOutcome.Rejected,
            instance.OrganizationId,
            threadId,
            messageId,
            positionId: instance.InboundDirectiveTarget,
            reasonCode: GitHubIssuesScopePolicy.ScopeDeniedCode,
            messageType: "github-issues.scope",
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["direction"] = "inbound",
                ["instanceId"] = instance.InstanceId,
                ["deniedDimension"] = deniedDimension,
                ["repository"] = canonicalRepository,
                ["redactions"] = "content,cursor,credentials,transport-diagnostics",
            },
            occurredAtUtc: observedAtUtc,
            idempotencyDiscriminator: identity + "\n" + deniedDimension));

        return new GitHubIssuesRepositoryPollResult(
            instance.InstanceId,
            canonicalRepository,
            GitHubIssuesRepositoryPollStatus.Ignored,
            0,
            0,
            nextPollAtUtc,
            GitHubIssuesScopePolicy.ScopeDeniedCode);
    }

    private static DateTimeOffset SafeAdd(DateTimeOffset value, TimeSpan interval)
    {
        try
        {
            return value.Add(interval);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.MaxValue.ToUniversalTime();
        }
    }
}

internal sealed class UnavailableGitHubIssuesInboundStore : IGitHubIssuesInboundStore
{
    public static UnavailableGitHubIssuesInboundStore Instance { get; } = new();

    private UnavailableGitHubIssuesInboundStore()
    {
    }

    public ValueTask<GitHubIssuesPollingCheckpoint?> ReadCheckpointAsync(
        string instanceId,
        string repository,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<GitHubIssuesPollingCheckpoint?>(Unavailable());

    public Task<GitHubIssuesInboundCommitResult> CommitBatchAsync(
        GitHubIssuesPollingCheckpoint? expectedCheckpoint,
        GitHubIssuesInboundBatch batch,
        DateTimeOffset capturedAtUtc,
        DateTimeOffset nextPollAtUtc,
        CancellationToken cancellationToken = default) =>
        Task.FromException<GitHubIssuesInboundCommitResult>(Unavailable());

    public Task<IReadOnlyList<GitHubIssuesInboundEnvelope>> ReadPendingAsync(
        string instanceId,
        string repository,
        int limit,
        CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<GitHubIssuesInboundEnvelope>>(Unavailable());

    public Task<bool> TryCompleteAsync(
        GitHubIssuesInboundEnvelope envelope,
        GitHubIssuesInboundCompletion completion,
        CancellationToken cancellationToken = default) =>
        Task.FromException<bool>(Unavailable());

    public ValueTask<GitHubIssueCorrelation?> FindCorrelationByIssueAsync(
        string instanceId,
        OrganizationId organizationId,
        string repository,
        long issueNumber,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<GitHubIssueCorrelation?>(Unavailable());

    public ValueTask<GitHubIssueCorrelation?> FindCorrelationByThreadAsync(
        string instanceId,
        OrganizationId organizationId,
        ThreadId threadId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<GitHubIssueCorrelation?>(Unavailable());

    public ValueTask<GitHubIssueCorrelation?> FindCorrelationByDirectiveAsync(
        string instanceId,
        OrganizationId organizationId,
        DirectiveId directiveId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<GitHubIssueCorrelation?>(Unavailable());

    private static InvalidOperationException Unavailable() =>
        new("ConnectionStrings:PostgreSql is required for durable GitHub Issues inbound polling.");
}
