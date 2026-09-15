using System.Collections.Immutable;
using Hive.Actors.Positions;
using Hive.Domain.Events;
using Hive.Domain.Identity;
using Hive.Infrastructure.Organization.ReadModels;

namespace Hive.Actors.Events;

/// <summary>Replays persisted operational history through the same fold as the live-state worker.</summary>
public sealed class PersistedPositionBlockedSource : IPositionBlockedSource
{
    private readonly IPositionLiveStateHistory _history;

    public PersistedPositionBlockedSource(IPositionLiveStateHistory history)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
    }

    public async ValueTask<ImmutableArray<CurrentPositionBlockedPeriod>> ReadAsync(OrganizationId organizationId,
        IReadOnlyCollection<PositionId> positionIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(positionIds);
        foreach (var positionId in positionIds) ArgumentNullException.ThrowIfNull(positionId);
        cancellationToken.ThrowIfCancellationRequested();
        if (positionIds.Count == 0) return [];
        var facts = await _history.ReadAsync(organizationId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var mapper = new PositionLiveStateFactMapper();
        foreach (var fact in facts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fact.OrganizationId != organizationId)
                throw new InvalidOperationException("Operational history returned a different organization.");
            mapper.Apply(fact);
        }

        return positionIds.Distinct().OrderBy(item => item.Value, StringComparer.Ordinal)
            .Select(position => mapper.CurrentBlockedPeriod(PositionEntityId.From(organizationId, position)))
            .OfType<CurrentPositionBlockedPeriod>().ToImmutableArray();
    }
}
