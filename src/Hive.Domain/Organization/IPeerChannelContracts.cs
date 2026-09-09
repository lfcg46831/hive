using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;

namespace Hive.Domain.Organization;

/// <summary>Read-only resolution of declared directional channels within an organization.</summary>
/// <remarks>
/// This seam does not decide admission, apply limits or synthesize same-unit/leadership channels.
/// Technical failures remain exceptions and must never be converted to a missing contract.
/// </remarks>
public interface IPeerChannelContracts
{
    /// <summary>Returns the incoming contract declared by the destination for the source unit.</summary>
    /// <returns>An immutable contract, or null if both units exist but this direction has no contract.</returns>
    /// <exception cref="PeerChannelContractNotFoundException">The organization or either unit is unknown.</exception>
    /// <exception cref="OperationCanceledException">The cancellation token was cancelled.</exception>
    ValueTask<PeerChannelConfiguration?> ResolveChannelAsync(
        OrganizationId organizationId,
        UnitId fromUnitId,
        UnitId toUnitId,
        CancellationToken cancellationToken = default);
}
