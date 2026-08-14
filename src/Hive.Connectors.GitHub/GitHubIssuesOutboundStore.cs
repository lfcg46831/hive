using Hive.Domain.Identity;

namespace Hive.Connectors.GitHub;

internal enum GitHubIssuesOutboundOperationState
{
    Pending = 1,
    Succeeded = 2,
    Rejected = 3,
}

internal sealed record GitHubIssuesOutboundOperationDescriptor
{
    public GitHubIssuesOutboundOperationDescriptor(
        string operationKey,
        string payloadHash,
        GitHubIssueCorrelation issue,
        PositionId positionId,
        DirectiveId directiveId,
        string toolName,
        DateTimeOffset createdAtUtc)
    {
        OperationKey = RequireDigest(operationKey, nameof(operationKey));
        PayloadHash = RequireDigest(payloadHash, nameof(payloadHash));
        Issue = issue ?? throw new ArgumentNullException(nameof(issue));
        PositionId = positionId ?? throw new ArgumentNullException(nameof(positionId));
        DirectiveId = directiveId ?? throw new ArgumentNullException(nameof(directiveId));
        if (!GitHubIssuesOutboundOperations.IsSupported(toolName))
        {
            throw new ArgumentOutOfRangeException(nameof(toolName), toolName, "Unknown GitHub Issues outbound operation.");
        }

        if (createdAtUtc == default || createdAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Created timestamp must be UTC.", nameof(createdAtUtc));
        }

        ToolName = toolName;
        CreatedAtUtc = createdAtUtc;
    }

    public string OperationKey { get; }

    public string PayloadHash { get; }

    public GitHubIssueCorrelation Issue { get; }

    public PositionId PositionId { get; }

    public DirectiveId DirectiveId { get; }

    public string ToolName { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    private static string RequireDigest(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9')
            and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("Value must be a lowercase SHA-256 digest.", parameterName);
        }

        return value;
    }
}

internal sealed record GitHubIssuesOutboundOperationSnapshot(
    GitHubIssuesOutboundOperationState State,
    int AttemptCount,
    string? LastCode,
    string? Receipt);

internal interface IGitHubIssuesOutboundOperationLease : IAsyncDisposable
{
    GitHubIssuesOutboundOperationSnapshot Snapshot { get; }

    Task RecordAttemptAsync(
        string code,
        DateTimeOffset attemptedAtUtc,
        CancellationToken cancellationToken = default);

    Task CompleteSuccessAsync(
        string receipt,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default);

    Task CompleteRejectedAsync(
        string errorCode,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default);
}

internal interface IGitHubIssuesOutboundStore
{
    Task<IGitHubIssuesOutboundOperationLease> AcquireAsync(
        GitHubIssuesOutboundOperationDescriptor descriptor,
        CancellationToken cancellationToken = default);
}

internal sealed class UnavailableGitHubIssuesOutboundStore : IGitHubIssuesOutboundStore
{
    public static UnavailableGitHubIssuesOutboundStore Instance { get; } = new();

    private UnavailableGitHubIssuesOutboundStore()
    {
    }

    public Task<IGitHubIssuesOutboundOperationLease> AcquireAsync(
        GitHubIssuesOutboundOperationDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<IGitHubIssuesOutboundOperationLease>(
            new InvalidOperationException(
                "ConnectionStrings:PostgreSql is required for durable GitHub Issues outbound execution."));
    }
}

internal sealed class GitHubIssuesOutboundOperationConflictException : InvalidOperationException
{
    public GitHubIssuesOutboundOperationConflictException()
        : base("The persisted GitHub outbound operation conflicts with the requested operation.")
    {
    }
}

