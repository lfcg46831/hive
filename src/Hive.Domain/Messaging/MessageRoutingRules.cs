using System.Collections.Immutable;

namespace Hive.Domain.Messaging;

public sealed record RoutingPathRule
{
    internal RoutingPathRule(
        Type fromEndpointType,
        Type toEndpointType,
        RoutingRelation relation)
    {
        FromEndpointType = fromEndpointType;
        ToEndpointType = toEndpointType;
        Relation = relation;
    }

    public Type FromEndpointType { get; }

    public Type ToEndpointType { get; }

    public RoutingRelation Relation { get; }
}

public sealed record MessageRoutingRule
{
    internal MessageRoutingRule(
        Type messageType,
        MessageChannel channel,
        ImmutableArray<RoutingPathRule> paths)
    {
        MessageType = messageType;
        Channel = channel;
        Paths = paths;
    }

    public Type MessageType { get; }

    public MessageChannel Channel { get; }

    public ImmutableArray<RoutingPathRule> Paths { get; }
}

public static class MessageRoutingRules
{
    public static ImmutableDictionary<Type, MessageRoutingRule> All { get; } =
        new[]
        {
            Rule<Directive>(
                MessageChannel.Vertical,
                Path<PositionEndpointRef, PositionEndpointRef>(
                    RoutingRelation.DirectSuperiorToDirectSubordinate)),
            Rule<Report>(
                MessageChannel.Vertical,
                Path<PositionEndpointRef, PositionEndpointRef>(
                    RoutingRelation.DirectSubordinateToDirectSuperior)),
            Rule<Escalation>(
                MessageChannel.Vertical,
                Path<PositionEndpointRef, PositionEndpointRef>(
                    RoutingRelation.DirectSubordinateToDirectSuperior),
                Path<PositionEndpointRef, OrganizationOwnerEndpointRef>(
                    RoutingRelation.RootLeadershipToOrganizationOwner)),
            Rule<ApprovalRequest>(
                MessageChannel.Governance,
                Path<PositionEndpointRef, PositionEndpointRef>(
                    RoutingRelation.RequesterToAuthorizedApprover),
                Path<PositionEndpointRef, OrganizationOwnerEndpointRef>(
                    RoutingRelation.RequesterToAuthorizedApprover)),
            Rule<ApprovalDecision>(
                MessageChannel.Governance,
                Path<PositionEndpointRef, PositionEndpointRef>(
                    RoutingRelation.AuthorizedApproverToOriginalRequester),
                Path<OrganizationOwnerEndpointRef, PositionEndpointRef>(
                    RoutingRelation.AuthorizedApproverToOriginalRequester)),
        }.ToImmutableDictionary(rule => rule.MessageType);

    public static MessageRoutingRule For<TMessage>()
        where TMessage : OrgMessage =>
        For(typeof(TMessage));

    public static MessageRoutingRule For(Type messageType)
    {
        ArgumentNullException.ThrowIfNull(messageType);

        if (All.TryGetValue(messageType, out var rule))
        {
            return rule;
        }

        throw new ArgumentException(
            $"{messageType.Name} has no vertical or governance routing rule.",
            nameof(messageType));
    }

    private static MessageRoutingRule Rule<TMessage>(
        MessageChannel channel,
        params RoutingPathRule[] paths)
        where TMessage : OrgMessage =>
        new(typeof(TMessage), channel, [.. paths]);

    private static RoutingPathRule Path<TFrom, TTo>(RoutingRelation relation)
        where TFrom : EndpointRef
        where TTo : EndpointRef =>
        new(typeof(TFrom), typeof(TTo), relation);
}
