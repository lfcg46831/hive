using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class MessageRoutingRulesTests
{
    [Fact]
    public void Routing_relations_are_a_closed_set()
    {
        Assert.Equal(1, (int)RoutingRelation.DirectSuperiorToDirectSubordinate);
        Assert.Equal(2, (int)RoutingRelation.DirectSubordinateToDirectSuperior);
        Assert.Equal(3, (int)RoutingRelation.RootLeadershipToOrganizationOwner);
        Assert.Equal(4, (int)RoutingRelation.RequesterToAuthorizedApprover);
        Assert.Equal(5, (int)RoutingRelation.AuthorizedApproverToOriginalRequester);
        Assert.Equal(6, (int)RoutingRelation.EscalationRecipientToOriginalRequester);
        Assert.Equal(
            [
                RoutingRelation.DirectSuperiorToDirectSubordinate,
                RoutingRelation.DirectSubordinateToDirectSuperior,
                RoutingRelation.RootLeadershipToOrganizationOwner,
                RoutingRelation.RequesterToAuthorizedApprover,
                RoutingRelation.AuthorizedApproverToOriginalRequester,
                RoutingRelation.EscalationRecipientToOriginalRequester,
            ],
            Enum.GetValues<RoutingRelation>());
    }

    [Fact]
    public void Catalog_contains_only_vertical_and_governance_message_types()
    {
        Assert.Equal(
            [
                typeof(ApprovalDecision),
                typeof(ApprovalRequest),
                typeof(AuthorizationDenial),
                typeof(AuthorizationGrant),
                typeof(Directive),
                typeof(Escalation),
                typeof(Report),
            ],
            MessageRoutingRules.All.Keys.OrderBy(type => type.Name));
    }

    [Fact]
    public void Vertical_matrix_defines_downward_upward_and_root_escalation_paths()
    {
        AssertRule<Directive>(
            MessageChannel.Vertical,
            Path<PositionEndpointRef, PositionEndpointRef>(
                RoutingRelation.DirectSuperiorToDirectSubordinate));
        AssertRule<Report>(
            MessageChannel.Vertical,
            Path<PositionEndpointRef, PositionEndpointRef>(
                RoutingRelation.DirectSubordinateToDirectSuperior));
        AssertRule<Escalation>(
            MessageChannel.Vertical,
            Path<PositionEndpointRef, PositionEndpointRef>(
                RoutingRelation.DirectSubordinateToDirectSuperior),
            Path<PositionEndpointRef, OrganizationOwnerEndpointRef>(
                RoutingRelation.RootLeadershipToOrganizationOwner));
    }

    [Fact]
    public void Governance_matrix_defines_authorized_request_and_decision_paths()
    {
        AssertRule<ApprovalRequest>(
            MessageChannel.Governance,
            Path<PositionEndpointRef, PositionEndpointRef>(
                RoutingRelation.RequesterToAuthorizedApprover),
            Path<PositionEndpointRef, OrganizationOwnerEndpointRef>(
                RoutingRelation.RequesterToAuthorizedApprover));
        AssertRule<ApprovalDecision>(
            MessageChannel.Governance,
            Path<PositionEndpointRef, PositionEndpointRef>(
                RoutingRelation.AuthorizedApproverToOriginalRequester),
            Path<OrganizationOwnerEndpointRef, PositionEndpointRef>(
                RoutingRelation.AuthorizedApproverToOriginalRequester));
        AssertRule<AuthorizationGrant>(
            MessageChannel.Governance,
            Path<PositionEndpointRef, PositionEndpointRef>(
                RoutingRelation.EscalationRecipientToOriginalRequester),
            Path<OrganizationOwnerEndpointRef, PositionEndpointRef>(
                RoutingRelation.EscalationRecipientToOriginalRequester));
        AssertRule<AuthorizationDenial>(
            MessageChannel.Governance,
            Path<PositionEndpointRef, PositionEndpointRef>(
                RoutingRelation.EscalationRecipientToOriginalRequester),
            Path<OrganizationOwnerEndpointRef, PositionEndpointRef>(
                RoutingRelation.EscalationRecipientToOriginalRequester));
    }

    [Fact]
    public void Unknown_or_null_message_types_have_no_routing_rule()
    {
        Assert.Throws<ArgumentNullException>(() => MessageRoutingRules.For(null!));
        Assert.Throws<ArgumentException>(() => MessageRoutingRules.For(typeof(Memo)));
    }

    private static void AssertRule<TMessage>(
        MessageChannel channel,
        params ExpectedPath[] expectedPaths)
        where TMessage : OrgMessage
    {
        var rule = MessageRoutingRules.For<TMessage>();

        Assert.Equal(typeof(TMessage), rule.MessageType);
        Assert.Equal(channel, rule.Channel);
        Assert.Equal(
            expectedPaths,
            rule.Paths.Select(path => new ExpectedPath(
                path.FromEndpointType,
                path.ToEndpointType,
                path.Relation)));
    }

    private static ExpectedPath Path<TFrom, TTo>(RoutingRelation relation)
        where TFrom : EndpointRef
        where TTo : EndpointRef =>
        new(typeof(TFrom), typeof(TTo), relation);

    private sealed record ExpectedPath(
        Type FromEndpointType,
        Type ToEndpointType,
        RoutingRelation Relation);
}
