using Hive.Domain.Identity;

namespace Hive.Infrastructure.Organization.ReadModels;

public interface IPositionLiveStateWriter
{
    ValueTask<PositionLiveStateSnapshot> AdvanceAsync(
        OrganizationId organizationId,
        PositionId positionId,
        PositionLiveState state,
        DateTimeOffset updatedAtUtc,
        PositionLiveStateCorrelatedEvent? correlatedEvent = null,
        CancellationToken cancellationToken = default);
}
