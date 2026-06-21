namespace Hive.Domain.Messaging;

public enum RoutingRelation
{
    DirectSuperiorToDirectSubordinate = 1,
    DirectSubordinateToDirectSuperior = 2,
    RootLeadershipToOrganizationOwner = 3,
    RequesterToAuthorizedApprover = 4,
    AuthorizedApproverToOriginalRequester = 5,
}
