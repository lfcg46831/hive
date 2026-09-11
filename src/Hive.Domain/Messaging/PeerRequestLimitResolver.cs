using Hive.Domain.Organization;
using Hive.Domain.Organization.Configuration;

namespace Hive.Domain.Messaging;

/// <summary>Resolves only declared request limits; general horizontal route gating belongs to admission.</summary>
public sealed class PeerRequestLimitResolver(IOrganizationRelations relations, IPeerChannelContracts contracts)
{
    private readonly IOrganizationRelations _relations = relations ?? throw new ArgumentNullException(nameof(relations));
    private readonly IPeerChannelContracts _contracts = contracts ?? throw new ArgumentNullException(nameof(contracts));

    public async ValueTask<PeerRequestChannel?> ResolveAsync(PeerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.From is not PositionEndpointRef from || request.To is not PositionEndpointRef to)
            return null;
        var fromUnit = await _relations.GetUnitOfPositionAsync(request.OrganizationId, from.PositionId, cancellationToken);
        var toUnit = await _relations.GetUnitOfPositionAsync(request.OrganizationId, to.PositionId, cancellationToken);
        if (fromUnit is null || toUnit is null || fromUnit == toUnit) return null;
        var fromLeader = await _relations.GetUnitLeadershipAsync(request.OrganizationId, fromUnit, cancellationToken);
        var toLeader = await _relations.GetUnitLeadershipAsync(request.OrganizationId, toUnit, cancellationToken);
        if (from.PositionId == fromLeader && to.PositionId == toLeader) return null;
        var contract = await _contracts.ResolveChannelAsync(request.OrganizationId, fromUnit, toUnit, cancellationToken);
        return contract is not null && contract.Types.Contains(PeerChannelMessageType.PeerRequest)
            ? new PeerRequestChannel(fromUnit, toUnit, contract.MaxOpenRequests)
            : null;
    }
}
