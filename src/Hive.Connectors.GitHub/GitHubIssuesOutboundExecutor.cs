using System.Security.Cryptography;
using System.Text;
using Hive.Domain.Auditing;
using Hive.Infrastructure.Connectors;

namespace Hive.Connectors.GitHub;

internal interface IGitHubIssuesOutboundExecutor
{
    ValueTask<ConnectorToolResult> ExecuteAsync(
        ConnectorToolInvocation invocation,
        CancellationToken cancellationToken = default);
}

internal interface IGitHubIssuesOutboundBackoff
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}

internal sealed class GitHubIssuesOutboundBackoff : IGitHubIssuesOutboundBackoff
{
    private readonly TimeProvider _timeProvider;

    public GitHubIssuesOutboundBackoff(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
        Task.Delay(delay, _timeProvider, cancellationToken);
}

internal sealed class GitHubIssuesOutboundExecutor : IGitHubIssuesOutboundExecutor
{
    internal const int MaximumAttempts = 3;
    internal static readonly TimeSpan InitialBackoff = TimeSpan.FromMilliseconds(100);

    private readonly GitHubIssuesConnectorConfigurationCatalog _catalog;
    private readonly IGitHubIssuesInboundStore _correlations;
    private readonly IGitHubIssuesOutboundStore _store;
    private readonly IGitHubIssuesOutboundClient _client;
    private readonly IGitHubIssuesOutboundBackoff _backoff;
    private readonly IJourneyAuditLog _auditLog;
    private readonly TimeProvider _timeProvider;

    public GitHubIssuesOutboundExecutor(
        GitHubIssuesConnectorConfigurationCatalog catalog,
        IGitHubIssuesInboundStore correlations,
        IGitHubIssuesOutboundStore store,
        IGitHubIssuesOutboundClient client,
        IGitHubIssuesOutboundBackoff backoff,
        IJourneyAuditLog auditLog,
        TimeProvider timeProvider)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _correlations = correlations ?? throw new ArgumentNullException(nameof(correlations));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _backoff = backoff ?? throw new ArgumentNullException(nameof(backoff));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask<ConnectorToolResult> ExecuteAsync(
        ConnectorToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        cancellationToken.ThrowIfCancellationRequested();

        if (!GitHubIssuesOutboundOperation.TryParse(
                invocation.ToolCall,
                out var operation,
                out var argumentError))
        {
            AuditFinal(invocation, argumentError!, issue: null, attempt: 0, succeeded: false);
            return ConnectorToolResult.Failed(argumentError!);
        }

        ResolvedIssue? resolved;
        try
        {
            resolved = await ResolveIssueAsync(invocation, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (GitHubIssuesOutboundCorrelationAmbiguousException)
        {
            const string code = "github-outbound-correlation-ambiguous";
            AuditFinal(invocation, code, issue: null, attempt: 0, succeeded: false);
            return ConnectorToolResult.Failed(code);
        }
        catch (Exception)
        {
            const string code = "github-outbound-correlation-unavailable";
            AuditFinal(invocation, code, issue: null, attempt: 0, succeeded: false);
            return ConnectorToolResult.Failed(code, retryable: true);
        }

        if (resolved is null)
        {
            const string code = "github-outbound-correlation-not-found";
            AuditFinal(invocation, code, issue: null, attempt: 0, succeeded: false);
            return ConnectorToolResult.Failed(code);
        }

        var scope = GitHubIssuesScopePolicy.AuthorizeOutbound(
            resolved.Instance,
            resolved.Issue,
            operation!.Name);
        if (!scope.IsAllowed)
        {
            AuditScopeDenied(
                invocation,
                resolved.Issue,
                scope.DeniedDimension!);
            return ConnectorToolResult.Failed(GitHubIssuesScopePolicy.ScopeDeniedCode);
        }

        var now = _timeProvider.GetUtcNow();
        var descriptor = new GitHubIssuesOutboundOperationDescriptor(
            invocation.OperationKey,
            PayloadHash(operation.CanonicalPayload),
            resolved.Issue,
            invocation.PositionId,
            invocation.DirectiveId,
            operation.Name,
            now);

        IGitHubIssuesOutboundOperationLease lease;
        try
        {
            lease = await _store.AcquireAsync(descriptor, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (GitHubIssuesOutboundOperationConflictException)
        {
            const string code = "github-outbound-operation-conflict";
            AuditFinal(invocation, code, resolved.Issue, attempt: 0, succeeded: false);
            return ConnectorToolResult.Failed(code);
        }
        catch (Exception)
        {
            const string code = "github-outbound-store-unavailable";
            AuditFinal(invocation, code, resolved.Issue, attempt: 0, succeeded: false);
            return ConnectorToolResult.Failed(code, retryable: true);
        }

        await using (lease)
        {
            if (lease.Snapshot.State == GitHubIssuesOutboundOperationState.Succeeded)
            {
                const string code = "github-outbound-duplicate-suppressed";
                AuditFinal(invocation, code, resolved.Issue, attempt: 0, succeeded: true);
                return Success(invocation.OperationKey, lease.Snapshot.Receipt!, "already-succeeded");
            }

            if (lease.Snapshot.State == GitHubIssuesOutboundOperationState.Rejected)
            {
                var code = lease.Snapshot.LastCode ?? "github-outbound-operation-rejected";
                AuditFinal(invocation, code, resolved.Issue, attempt: 0, succeeded: false);
                return ConnectorToolResult.Failed(code);
            }

            GitHubIssuesOutboundClientResult? last = null;
            for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    last = await _client.ExecuteAsync(
                            new GitHubIssuesOutboundRequest(
                                invocation.OperationKey,
                                resolved.Instance,
                                resolved.Issue,
                                operation),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    last = GitHubIssuesOutboundClientResult.Failed(
                        "github-outbound-client-failed",
                        retryable: true);
                }

                var attemptCode = last.Succeeded
                    ? "github-outbound-published"
                    : last.ErrorCode ?? "github-outbound-client-result-invalid";
                var attemptedAt = _timeProvider.GetUtcNow();
                await lease.RecordAttemptAsync(attemptCode, attemptedAt, cancellationToken)
                    .ConfigureAwait(false);
                AuditAttempt(invocation, attemptCode, resolved.Issue, attempt, last);

                if (last.Succeeded)
                {
                    await lease.CompleteSuccessAsync(
                            last.Receipt!,
                            _timeProvider.GetUtcNow(),
                            cancellationToken)
                        .ConfigureAwait(false);
                    AuditFinal(invocation, attemptCode, resolved.Issue, attempt, succeeded: true);
                    return Success(invocation.OperationKey, last.Receipt!, "succeeded");
                }

                if (!last.Retryable)
                {
                    await lease.CompleteRejectedAsync(
                            attemptCode,
                            _timeProvider.GetUtcNow(),
                            cancellationToken)
                        .ConfigureAwait(false);
                    AuditFinal(invocation, attemptCode, resolved.Issue, attempt, succeeded: false);
                    return ConnectorToolResult.Failed(attemptCode);
                }

                if (attempt < MaximumAttempts)
                {
                    await _backoff.DelayAsync(
                            TimeSpan.FromTicks(InitialBackoff.Ticks << (attempt - 1)),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            const string exhausted = "github-outbound-retry-exhausted";
            AuditFinal(invocation, exhausted, resolved.Issue, MaximumAttempts, succeeded: false);
            return ConnectorToolResult.Failed(exhausted, retryable: true);
        }
    }

    private async Task<ResolvedIssue?> ResolveIssueAsync(
        ConnectorToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        var matches = new List<ResolvedIssue>();
        foreach (var instance in _catalog.Instances.Where(candidate =>
                     candidate.OrganizationId == invocation.OrganizationId))
        {
            var issue = await _correlations.FindCorrelationByDirectiveAsync(
                    instance.InstanceId,
                    invocation.OrganizationId,
                    invocation.DirectiveId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (issue is null && invocation.ParentDirectiveId is { } parentDirectiveId)
            {
                issue = await _correlations.FindCorrelationByDirectiveAsync(
                        instance.InstanceId,
                        invocation.OrganizationId,
                        parentDirectiveId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            issue ??= await _correlations.FindCorrelationByThreadAsync(
                    instance.InstanceId,
                    invocation.OrganizationId,
                    invocation.ThreadId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (issue is not null && issue.ThreadId == invocation.ThreadId)
            {
                matches.Add(new ResolvedIssue(instance, issue));
            }
        }

        var distinct = matches
            .GroupBy(match => (
                match.Instance.InstanceId,
                match.Issue.Repository,
                match.Issue.IssueNumber))
            .Select(group => group.First())
            .ToArray();
        if (distinct.Length > 1)
        {
            throw new GitHubIssuesOutboundCorrelationAmbiguousException();
        }

        return distinct.SingleOrDefault();
    }

    private void AuditAttempt(
        ConnectorToolInvocation invocation,
        string code,
        GitHubIssueCorrelation issue,
        int attempt,
        GitHubIssuesOutboundClientResult result) =>
        AppendAudit(
            invocation,
            result.Succeeded ? JourneyAuditOutcome.Succeeded : JourneyAuditOutcome.Failed,
            code,
            issue,
            attempt,
            $"{invocation.OperationKey}:attempt:{attempt}",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["retryable"] = result.Retryable ? "true" : "false",
            });

    private void AuditFinal(
        ConnectorToolInvocation invocation,
        string code,
        GitHubIssueCorrelation? issue,
        int attempt,
        bool succeeded) =>
        AppendAudit(
            invocation,
            succeeded ? JourneyAuditOutcome.Succeeded : JourneyAuditOutcome.Failed,
            code,
            issue,
            attempt,
            $"{invocation.OperationKey}:final:{code}");

    private void AuditScopeDenied(
        ConnectorToolInvocation invocation,
        GitHubIssueCorrelation issue,
        string deniedDimension) =>
        AppendAudit(
            invocation,
            JourneyAuditOutcome.Rejected,
            GitHubIssuesScopePolicy.ScopeDeniedCode,
            issue,
            attempt: 0,
            $"{invocation.OperationKey}:scope-denied:{deniedDimension}",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["direction"] = "outbound",
                ["deniedDimension"] = deniedDimension,
            });

    private void AppendAudit(
        ConnectorToolInvocation invocation,
        JourneyAuditOutcome outcome,
        string code,
        GitHubIssueCorrelation? issue,
        int attempt,
        string discriminator,
        IReadOnlyDictionary<string, string>? extra = null)
    {
        var payload = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["operation"] = invocation.ToolCall.Name,
            ["operationKey"] = invocation.OperationKey,
            ["attempt"] = attempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["instanceId"] = issue?.InstanceId ?? "unresolved",
            ["repository"] = issue?.Repository ?? "unresolved",
            ["issueNumber"] = issue?.IssueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unresolved",
            ["redactions"] = "arguments,receipt,credentials,transport-diagnostics",
        };
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                payload.Add(key, value);
            }
        }

        _auditLog.Append(JourneyAuditRecord.Create(
            JourneyAuditStage.ConnectorOutbound,
            outcome,
            invocation.OrganizationId,
            invocation.ThreadId,
            invocation.SourceMessageId,
            invocation.DirectiveId,
            invocation.PositionId,
            code,
            invocation.ToolCall.Name,
            payload: payload,
            occurredAtUtc: _timeProvider.GetUtcNow(),
            idempotencyDiscriminator: discriminator));
    }

    private static ConnectorToolResult Success(
        string operationKey,
        string receipt,
        string status) =>
        ConnectorToolResult.Succeeded(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["operation_key"] = operationKey,
                ["receipt"] = receipt,
                ["status"] = status,
            });

    private static string PayloadHash(string canonicalPayload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload)))
            .ToLowerInvariant();

    private sealed record ResolvedIssue(
        GitHubIssuesConnectorInstanceConfiguration Instance,
        GitHubIssueCorrelation Issue);

    private sealed class GitHubIssuesOutboundCorrelationAmbiguousException : Exception;
}
