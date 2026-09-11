namespace Hive.Domain.Messaging;

public enum RoutingRelation
{
    DirectSuperiorToDirectSubordinate = 1,
    DirectSubordinateToDirectSuperior = 2,
    RootLeadershipToOrganizationOwner = 3,
    RequesterToAuthorizedApprover = 4,
    AuthorizedApproverToOriginalRequester = 5,
    EscalationRecipientToOriginalRequester = 6,

    /// <summary>Both positions belong to the same unit; no peer channel contract is required.</summary>
    SameUnit = 7,

    /// <summary>
    /// Both positions lead their respective, different units; the implicit mediation channel
    /// requires neither a declared contract nor a declarative limit.
    /// </summary>
    UnitLeadershipToUnitLeadership = 8,

    /// <summary>
    /// Positions belong to different units and the destination unit declares a channel from the
    /// source unit permitting the concrete message type. The reverse direction is independent.
    /// </summary>
    DeclaredPeerChannel = 9,

    /// <summary>
    /// The original recipient replies to the original requester of an accepted, open PeerRequest
    /// in the same organization and thread. Correlation is required even within one unit or between
    /// leaders; no reverse channel contract is required.
    /// </summary>
    PeerRequestRecipientToOriginalRequester = 10,
}
