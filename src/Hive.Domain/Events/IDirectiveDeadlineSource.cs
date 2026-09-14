using System.Collections.Immutable;
using Hive.Domain.Identity;

namespace Hive.Domain.Events;

/// <summary>Metadata of a persisted, unanswered directive addressed to its subscribing position.</summary>
public sealed record OpenDirectiveDeadline
{
    public OpenDirectiveDeadline(OrganizationId organizationId, PositionId positionId,
        EventSourceCorrelation correlation, DateTimeOffset deadlineAtUtc)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(positionId);
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(correlation.DirectiveId);
        OrganizationId = organizationId;
        PositionId = positionId;
        Correlation = correlation;
        DeadlineAtUtc = deadlineAtUtc.ToUniversalTime();
    }

    public OrganizationId OrganizationId { get; }
    public PositionId PositionId { get; }
    public EventSourceCorrelation Correlation { get; }
    public DateTimeOffset DeadlineAtUtc { get; }
}

/// <summary>
/// Reads all current candidates from persisted inbox state at one consistent snapshot, including
/// source directive identities. No feed cursor: unchanged input and late commits are revisited.
/// Confirmed absence is empty; unavailable or invalid source data and cancellation propagate.
/// </summary>
public interface IDirectiveDeadlineSource
{
    ValueTask<ImmutableArray<OpenDirectiveDeadline>> ReadAsync(OrganizationId organizationId,
        IReadOnlyCollection<PositionId> positionIds, DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken = default);
}
