using Hive.Domain.Identity;
using Hive.Domain.Organization;
using Hive.Domain.Organization.Configuration;

namespace Hive.Domain.Messaging;

/// <summary>Resolves declared escalation without inferring policy from a missing channel.</summary>
public sealed class PeerRequestRejectionPolicy(IOrganizationRelations relations, IPeerChannelContracts contracts)
{
    public async ValueTask<bool> ShouldEscalateAsync(OrgMessage message, RoutingRejection rejection,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (message is not PeerRequest { From: PositionEndpointRef from, To: PositionEndpointRef to }
            || !rejection.AuditResult.Errors.Any(error => error.Code is
                "peer-channel-required" or "peer-type-not-allowed" or "peer-channel-limit-exceeded"))
            return false;
        var source = await relations.GetUnitOfPositionAsync(message.OrganizationId, from.PositionId, cancellationToken);
        var destination = await relations.GetUnitOfPositionAsync(message.OrganizationId, to.PositionId, cancellationToken);
        if (source is null || destination is null || source == destination) return false;
        var channel = await contracts.ResolveChannelAsync(message.OrganizationId, source, destination, cancellationToken);
        return channel?.OnRejection == PeerChannelRejectionAction.Escalate;
    }

    public ValueTask<PositionId?> GetSuperiorAsync(OrganizationId organization, PositionId requester,
        CancellationToken cancellationToken = default) =>
        relations.GetDirectSuperiorAsync(organization, requester, cancellationToken);
}
