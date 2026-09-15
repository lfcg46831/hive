using System.Collections.Immutable;
using Hive.Domain.Identity;

namespace Hive.Domain.Events;

/// <summary>A current continuous Blocked period derived exclusively from persisted state facts.</summary>
public sealed record CurrentPositionBlockedPeriod
{
    public CurrentPositionBlockedPeriod(OrganizationId organizationId, PositionId positionId,
        DateTimeOffset blockedSinceUtc, PositionBlockedCause cause, EventSourceCorrelation? correlation = null)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(positionId);
        _ = PositionBlockedCauseContract.ToWireValue(cause);
        if (cause == PositionBlockedCause.PendingEscalation && correlation is null)
            throw new ArgumentException("A pending escalation requires source correlation.", nameof(correlation));
        if (blockedSinceUtc == default)
            throw new ArgumentException("The period start must be specified.", nameof(blockedSinceUtc));
        OrganizationId = organizationId;
        PositionId = positionId;
        BlockedSinceUtc = blockedSinceUtc.ToUniversalTime();
        Cause = cause;
        Correlation = correlation;
    }

    public OrganizationId OrganizationId { get; }
    public PositionId PositionId { get; }
    public DateTimeOffset BlockedSinceUtc { get; }
    public PositionBlockedCause Cause { get; }
    public EventSourceCorrelation? Correlation { get; }
}

/// <summary>
/// Returns all current periods for the requested subscribing positions at one persisted snapshot.
/// Cause changes preserve the start; leaving Blocked ends the period. Absence is empty, whereas
/// invalid data, unavailability and cancellation propagate. No ingestion cursor excludes late facts.
/// </summary>
public interface IPositionBlockedSource
{
    ValueTask<ImmutableArray<CurrentPositionBlockedPeriod>> ReadAsync(OrganizationId organizationId,
        IReadOnlyCollection<PositionId> positionIds, CancellationToken cancellationToken = default);
}
