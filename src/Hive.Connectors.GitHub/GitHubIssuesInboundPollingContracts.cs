using System.Collections.Immutable;
using System.Text.Json;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Connectors.GitHub;

internal static class GitHubIssuesInboundEventKinds
{
    public const string Issue = "issue";
    public const string Comment = "comment";

    public static bool IsSupported(string? value) =>
        value is Issue or Comment;
}

/// <summary>
/// Opaque external event captured by the polling transport. T03 deliberately validates only the
/// transport identity, the closed event kind and JSON shape; T04 owns interpretation and mapping.
/// </summary>
internal sealed record GitHubIssuesInboundEvent
{
    public GitHubIssuesInboundEvent(
        string externalEventId,
        string kind,
        string payloadJson)
    {
        ExternalEventId = RequireText(
            externalEventId,
            nameof(externalEventId),
            maximumLength: 512);
        if (!GitHubIssuesInboundEventKinds.IsSupported(kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "GitHub inbound event kind must be 'issue' or 'comment'.");
        }

        Kind = kind;
        PayloadJson = RequireJsonObject(payloadJson, nameof(payloadJson));
    }

    public string ExternalEventId { get; }

    public string Kind { get; }

    public string PayloadJson { get; }

    private static string RequireText(string value, string parameterName, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Length > maximumLength
            || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"Value must be trimmed, non-empty, at most {maximumLength} characters and contain no control characters.",
                parameterName);
        }

        return value;
    }

    private static string RequireJsonObject(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                throw new ArgumentException(
                    "GitHub inbound event payload must be a JSON object.",
                    parameterName);
            }

            return document.RootElement.GetRawText();
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "GitHub inbound event payload must be valid JSON.",
                parameterName,
                exception);
        }
    }
}

/// <summary>
/// Replay-safe batch returned by the T08 client seam. The cursor is opaque to T03 and must be
/// interpreted inclusively by the client so boundary events may be replayed safely.
/// </summary>
internal sealed record GitHubIssuesInboundBatch
{
    public GitHubIssuesInboundBatch(
        string instanceId,
        string repository,
        string? nextCursor,
        IReadOnlyList<GitHubIssuesInboundEvent> events,
        DateTimeOffset? rateLimitNotBeforeUtc = null)
    {
        InstanceId = GitHubIssuesConnectorInstanceConfiguration.RequireInstanceId(
            instanceId,
            nameof(instanceId));
        if (!GitHubIssuesConnectorInstanceConfiguration.IsValidRepository(repository))
        {
            throw new ArgumentException(
                "Repository must be a trimmed 'owner/repository' identifier.",
                nameof(repository));
        }

        ArgumentNullException.ThrowIfNull(events);
        Repository = repository;
        NextCursor = RequireOptionalCursor(nextCursor, nameof(nextCursor));
        Events = events.ToImmutableArray();
        var duplicate = Events
            .GroupBy(item => item.ExternalEventId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"External event id '{duplicate.Key}' occurs more than once in the batch.",
                nameof(events));
        }

        RateLimitNotBeforeUtc = RequireUtc(
            rateLimitNotBeforeUtc,
            nameof(rateLimitNotBeforeUtc));
    }

    public string InstanceId { get; }

    public string Repository { get; }

    public string? NextCursor { get; }

    public ImmutableArray<GitHubIssuesInboundEvent> Events { get; }

    public DateTimeOffset? RateLimitNotBeforeUtc { get; }

    internal static string? RequireOptionalCursor(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Length > 4096
            || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Polling cursor must be null or a trimmed non-empty value of at most 4096 characters without control characters.",
                parameterName);
        }

        return value;
    }

    internal static DateTimeOffset? RequireUtc(DateTimeOffset? value, string parameterName)
    {
        if (value is { Offset: not { Ticks: 0 } })
        {
            throw new ArgumentException(
                "Timestamp must use a UTC offset.",
                parameterName);
        }

        return value;
    }
}

internal sealed record GitHubIssuesPollingCheckpoint
{
    public GitHubIssuesPollingCheckpoint(
        string instanceId,
        string repository,
        string? cursor,
        DateTimeOffset notBeforeUtc)
    {
        InstanceId = GitHubIssuesConnectorInstanceConfiguration.RequireInstanceId(
            instanceId,
            nameof(instanceId));
        if (!GitHubIssuesConnectorInstanceConfiguration.IsValidRepository(repository))
        {
            throw new ArgumentException(
                "Repository must be a trimmed 'owner/repository' identifier.",
                nameof(repository));
        }

        if (notBeforeUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Checkpoint not-before timestamp must use a UTC offset.",
                nameof(notBeforeUtc));
        }

        Repository = repository;
        Cursor = GitHubIssuesInboundBatch.RequireOptionalCursor(cursor, nameof(cursor));
        NotBeforeUtc = notBeforeUtc;
    }

    public string InstanceId { get; }

    public string Repository { get; }

    public string? Cursor { get; }

    public DateTimeOffset NotBeforeUtc { get; }
}

internal sealed record GitHubIssuesInboundEnvelope(
    string InstanceId,
    string Repository,
    string ExternalEventId,
    string Kind,
    string PayloadJson,
    DateTimeOffset CapturedAtUtc);

internal sealed record GitHubIssueCorrelation
{
    public GitHubIssueCorrelation(
        string instanceId,
        OrganizationId organizationId,
        string repository,
        long issueNumber,
        ThreadId threadId,
        DirectiveId rootDirectiveId)
    {
        InstanceId = GitHubIssuesConnectorInstanceConfiguration.RequireInstanceId(
            instanceId,
            nameof(instanceId));
        OrganizationId = organizationId ?? throw new ArgumentNullException(nameof(organizationId));
        if (!GitHubIssuesConnectorInstanceConfiguration.IsValidRepository(repository))
        {
            throw new ArgumentException(
                "Repository must be a trimmed 'owner/repository' identifier.",
                nameof(repository));
        }

        if (issueNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(issueNumber),
                issueNumber,
                "GitHub issue number must be positive.");
        }

        Repository = repository.ToLowerInvariant();
        IssueNumber = issueNumber;
        ThreadId = threadId ?? throw new ArgumentNullException(nameof(threadId));
        RootDirectiveId = rootDirectiveId
            ?? throw new ArgumentNullException(nameof(rootDirectiveId));
    }

    public string InstanceId { get; }

    public OrganizationId OrganizationId { get; }

    public string Repository { get; }

    public long IssueNumber { get; }

    public ThreadId ThreadId { get; }

    public DirectiveId RootDirectiveId { get; }
}

internal sealed record GitHubIssueSubmissionCorrelation
{
    public GitHubIssueSubmissionCorrelation(
        GitHubIssueCorrelation issue,
        DirectiveId directiveId)
    {
        Issue = issue ?? throw new ArgumentNullException(nameof(issue));
        DirectiveId = directiveId ?? throw new ArgumentNullException(nameof(directiveId));
    }

    public GitHubIssueCorrelation Issue { get; }

    public DirectiveId DirectiveId { get; }
}

internal enum GitHubIssuesInboundCompletionState
{
    Submitted = 1,
    Rejected = 2,
}

internal sealed record GitHubIssuesInboundCompletion
{
    public GitHubIssuesInboundCompletion(
        GitHubIssuesInboundCompletionState state,
        DateTimeOffset completedAtUtc,
        string? reasonCode = null,
        GitHubIssueSubmissionCorrelation? submission = null)
    {
        if (state is not (GitHubIssuesInboundCompletionState.Submitted
            or GitHubIssuesInboundCompletionState.Rejected))
        {
            throw new ArgumentOutOfRangeException(
                nameof(state),
                state,
                "GitHub inbound completion state is undefined.");
        }

        if (completedAtUtc == default || completedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Completion timestamp must be specified and use a UTC offset.",
                nameof(completedAtUtc));
        }

        if (state is GitHubIssuesInboundCompletionState.Submitted
            && (reasonCode is not null || submission is null))
        {
            throw new ArgumentException(
                "Submitted GitHub inbound events require correlation and cannot carry a rejection reason.",
                nameof(submission));
        }

        if (state is GitHubIssuesInboundCompletionState.Rejected
            && (string.IsNullOrWhiteSpace(reasonCode)
                || !string.Equals(reasonCode, reasonCode.Trim(), StringComparison.Ordinal)
                || reasonCode.Any(char.IsWhiteSpace)
                || submission is not null))
        {
            throw new ArgumentException(
                "Rejected GitHub inbound events require a closed reason code and cannot carry correlation.",
                nameof(reasonCode));
        }

        State = state;
        CompletedAtUtc = completedAtUtc;
        ReasonCode = reasonCode;
        Submission = submission;
    }

    public GitHubIssuesInboundCompletionState State { get; }

    public DateTimeOffset CompletedAtUtc { get; }

    public string? ReasonCode { get; }

    public GitHubIssueSubmissionCorrelation? Submission { get; }
}

internal sealed record GitHubIssuesInboundCommitResult(
    bool IsApplied,
    int InsertedCount,
    GitHubIssuesPollingCheckpoint? Checkpoint)
{
    public static GitHubIssuesInboundCommitResult ConcurrentCheckpoint() =>
        new(false, 0, null);
}

internal enum GitHubIssuesRepositoryPollStatus
{
    Deferred = 0,
    Committed = 1,
    ConcurrentCheckpoint = 2,
    Failed = 3,
    Ignored = 4,
}

internal sealed record GitHubIssuesRepositoryPollResult(
    string InstanceId,
    string Repository,
    GitHubIssuesRepositoryPollStatus Status,
    int FetchedCount,
    int InsertedCount,
    DateTimeOffset NextPollAtUtc,
    string? ErrorCode = null);

internal sealed record GitHubIssuesPollingCycleResult
{
    public GitHubIssuesPollingCycleResult(
        IReadOnlyList<GitHubIssuesRepositoryPollResult> repositories)
    {
        ArgumentNullException.ThrowIfNull(repositories);
        Repositories = repositories.ToImmutableArray();
        NextPollAtUtc = Repositories.IsEmpty
            ? null
            : Repositories.Min(result => result.NextPollAtUtc);
    }

    public ImmutableArray<GitHubIssuesRepositoryPollResult> Repositories { get; }

    public DateTimeOffset? NextPollAtUtc { get; }
}

internal interface IGitHubIssuesInboundClient
{
    /// <summary>
    /// Fetches a replay-safe page. When <paramref name="cursor"/> is present, the client must read
    /// inclusively from that boundary; the store removes replayed external identities.
    /// </summary>
    Task<GitHubIssuesInboundBatch> FetchBatchAsync(
        GitHubIssuesConnectorInstanceConfiguration instance,
        string repository,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken = default);
}

internal interface IGitHubIssuesInboundStore
{
    ValueTask<GitHubIssuesPollingCheckpoint?> ReadCheckpointAsync(
        string instanceId,
        string repository,
        CancellationToken cancellationToken = default);

    Task<GitHubIssuesInboundCommitResult> CommitBatchAsync(
        GitHubIssuesPollingCheckpoint? expectedCheckpoint,
        GitHubIssuesInboundBatch batch,
        DateTimeOffset capturedAtUtc,
        DateTimeOffset nextPollAtUtc,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubIssuesInboundEnvelope>> ReadPendingAsync(
        string instanceId,
        string repository,
        int limit,
        CancellationToken cancellationToken = default);

    Task<bool> TryCompleteAsync(
        GitHubIssuesInboundEnvelope envelope,
        GitHubIssuesInboundCompletion completion,
        CancellationToken cancellationToken = default);

    ValueTask<GitHubIssueCorrelation?> FindCorrelationByIssueAsync(
        string instanceId,
        OrganizationId organizationId,
        string repository,
        long issueNumber,
        CancellationToken cancellationToken = default);

    ValueTask<GitHubIssueCorrelation?> FindCorrelationByThreadAsync(
        string instanceId,
        OrganizationId organizationId,
        ThreadId threadId,
        CancellationToken cancellationToken = default);

    ValueTask<GitHubIssueCorrelation?> FindCorrelationByDirectiveAsync(
        string instanceId,
        OrganizationId organizationId,
        DirectiveId directiveId,
        CancellationToken cancellationToken = default);
}

internal interface IGitHubIssuesInboundPoller
{
    Task<GitHubIssuesPollingCycleResult> PollDueRepositoriesAsync(
        CancellationToken cancellationToken = default);
}

internal interface IGitHubIssuesInboundProcessor
{
    Task<GitHubIssuesInboundProcessingCycleResult> ProcessPendingAsync(
        CancellationToken cancellationToken = default);
}
