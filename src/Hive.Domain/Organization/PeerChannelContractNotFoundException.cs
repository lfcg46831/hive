using Hive.Domain.Identity;

namespace Hive.Domain.Organization;

/// <summary>A structural lookup failure, distinct from confirmed absence of a declared channel.</summary>
public sealed class PeerChannelContractNotFoundException : Exception
{
    private PeerChannelContractNotFoundException(string message) : base(message) { }

    public static PeerChannelContractNotFoundException ForOrganization(OrganizationId organizationId)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        return new($"Organization '{organizationId.Value}' was not found in the peer channel registry.");
    }

    public static PeerChannelContractNotFoundException ForUnit(OrganizationId organizationId, UnitId unitId)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(unitId);
        return new($"Unit '{unitId.Value}' was not found in organization '{organizationId.Value}'.");
    }
}
