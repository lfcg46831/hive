using System.Collections.Immutable;
using Hive.Domain.Identity;

namespace Hive.Infrastructure.Organization.ReadModels;

/// <summary>One consistent snapshot of all committed operational facts, in persisted sequence order.</summary>
public interface IPositionLiveStateHistory
{
    ValueTask<ImmutableArray<PositionLiveStateProjectionFact>> ReadAsync(OrganizationId organizationId,
        CancellationToken cancellationToken = default);
}
