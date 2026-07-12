using System.Collections.Immutable;
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class MessageContractRulesTests
{
    private static readonly string[] CommonRequiredFields =
    [
        nameof(OrgMessage.Id),
        nameof(OrgMessage.OrganizationId),
        nameof(OrgMessage.From),
        nameof(OrgMessage.To),
        nameof(OrgMessage.Thread),
        nameof(OrgMessage.Priority),
        nameof(OrgMessage.SchemaVersion),
        nameof(OrgMessage.SentAt),
    ];

    public static TheoryData<Type, MessageChannel, string[], string[]> MessageContracts => new()
    {
        {
            typeof(Directive), MessageChannel.Vertical,
            [nameof(Directive.DirectiveId), nameof(Directive.Objective), nameof(Directive.Context)],
            [nameof(OrgMessage.Deadline), nameof(Directive.ParentDirectiveId)]
        },
        {
            typeof(Report), MessageChannel.Vertical,
            [nameof(Report.AboutDirectiveId), nameof(Report.Kind), nameof(Report.Body)],
            [nameof(OrgMessage.Deadline)]
        },
        {
            typeof(Escalation), MessageChannel.Vertical,
            [nameof(Escalation.Issue), nameof(Escalation.Context), nameof(Escalation.OptionsConsidered)],
            [nameof(OrgMessage.Deadline)]
        },
        {
            typeof(Memo), MessageChannel.Horizontal,
            [nameof(Memo.Body)],
            [nameof(OrgMessage.Deadline)]
        },
        {
            typeof(PeerRequest), MessageChannel.Horizontal,
            [nameof(PeerRequest.Ask)],
            [nameof(OrgMessage.Deadline)]
        },
        {
            typeof(PeerResponse), MessageChannel.Horizontal,
            [nameof(PeerResponse.InReplyTo), nameof(PeerResponse.Body)],
            [nameof(OrgMessage.Deadline)]
        },
        {
            typeof(ApprovalRequest), MessageChannel.Governance,
            [nameof(ApprovalRequest.Action), nameof(ApprovalRequest.Justification), nameof(ApprovalRequest.Policy)],
            [nameof(OrgMessage.Deadline)]
        },
        {
            typeof(ApprovalDecision), MessageChannel.Governance,
            [nameof(ApprovalDecision.RequestId), nameof(ApprovalDecision.Approved)],
            [nameof(OrgMessage.Deadline), nameof(ApprovalDecision.Reason)]
        },
        {
            typeof(AuthorizationGrant), MessageChannel.Governance,
            [
                nameof(AuthorizationGrant.InReplyTo), nameof(AuthorizationGrant.RetainedActionId),
                nameof(AuthorizationGrant.Fingerprint), nameof(AuthorizationGrant.Key),
                nameof(AuthorizationGrant.ExpiresAt)
            ],
            [nameof(OrgMessage.Deadline), nameof(AuthorizationGrant.Reason)]
        },
        {
            typeof(AuthorizationDenial), MessageChannel.Governance,
            [
                nameof(AuthorizationDenial.InReplyTo), nameof(AuthorizationDenial.RetainedActionId),
                nameof(AuthorizationDenial.Reason)
            ],
            [nameof(OrgMessage.Deadline)]
        },
        {
            typeof(Pulse), MessageChannel.System,
            [nameof(Pulse.ScheduleId), nameof(Pulse.Payload)],
            [nameof(OrgMessage.Deadline)]
        },
        {
            typeof(EventTrigger), MessageChannel.System,
            [nameof(EventTrigger.EventType), nameof(EventTrigger.Payload)],
            [nameof(OrgMessage.Deadline)]
        },
    };

    [Fact]
    public void Catalog_defines_exactly_the_canonical_message_taxonomy()
    {
        Assert.Equal(
            MessageContracts.Select(row => (Type)row[0]).OrderBy(type => type.Name),
            MessageContractRules.All.Keys.OrderBy(type => type.Name));
    }

    [Theory]
    [MemberData(nameof(MessageContracts))]
    public void Contract_defines_channel_and_required_fields(
        Type messageType,
        MessageChannel channel,
        string[] payloadRequiredFields,
        string[] optionalFields)
    {
        var contract = MessageContractRules.For(messageType);

        Assert.Equal(messageType, contract.MessageType);
        Assert.Equal(channel, contract.Channel);
        Assert.Equal(
            CommonRequiredFields.Concat(payloadRequiredFields),
            contract.RequiredFields);
        Assert.Equal(optionalFields, contract.OptionalFields);
    }

    [Fact]
    public void Catalog_defines_the_endpoint_matrix()
    {
        AssertEndpoints<Directive>(Position(), Position());
        AssertEndpoints<Report>(Position(), Position());
        AssertEndpoints<Escalation>(
            Position(),
            Position(), Owner());
        AssertEndpoints<Memo>(Position(), Position());
        AssertEndpoints<PeerRequest>(Position(), Position());
        AssertEndpoints<PeerResponse>(Position(), Position());
        AssertEndpoints<ApprovalRequest>(
            Position(),
            Position(), Owner());
        AssertEndpoints<ApprovalDecision>(
            [Position(), Owner()],
            [Position()]);
        AssertEndpoints<AuthorizationGrant>(
            [Position(), Owner()],
            [Position()]);
        AssertEndpoints<AuthorizationDenial>(
            [Position(), Owner()],
            [Position()]);
        AssertEndpoints<Pulse>(
            [System(SystemEndpointKind.Scheduler)],
            [Position()]);
        AssertEndpoints<EventTrigger>(
            [System(SystemEndpointKind.DomainEvents)],
            [Position()]);
    }

    [Fact]
    public void Directive_lineage_targets_its_immediate_parent_in_the_same_scope()
    {
        var relation = Assert.Single(MessageContractRules.For<Directive>().References);

        Assert.Equal(nameof(Directive.ParentDirectiveId), relation.SourceProperty);
        Assert.Equal(typeof(Directive), relation.TargetMessageType);
        Assert.Equal(nameof(Directive.DirectiveId), relation.TargetProperty);
        Assert.False(relation.IsRequired);
        Assert.True(relation.MustShareOrganization);
        Assert.True(relation.MustShareThread);
        Assert.True(relation.DisallowSelfReference);
        Assert.True(relation.DisallowCycles);
    }

    [Fact]
    public void Report_lineage_targets_an_exact_directive_in_the_same_scope()
    {
        var relation = Assert.Single(MessageContractRules.For<Report>().References);

        Assert.Equal(nameof(Report.AboutDirectiveId), relation.SourceProperty);
        Assert.Equal(typeof(Directive), relation.TargetMessageType);
        Assert.Equal(nameof(Directive.DirectiveId), relation.TargetProperty);
        Assert.True(relation.IsRequired);
        Assert.True(relation.MustShareOrganization);
        Assert.True(relation.MustShareThread);
        Assert.False(relation.DisallowSelfReference);
        Assert.False(relation.DisallowCycles);
    }

    [Fact]
    public void Approval_policy_is_required_payload_and_not_an_endpoint_variant()
    {
        var contract = MessageContractRules.For<ApprovalRequest>();

        Assert.Contains(nameof(ApprovalRequest.Policy), contract.RequiredFields);
        Assert.DoesNotContain(
            contract.From.Concat(contract.To),
            endpoint => endpoint.EndpointType.Name.Contains("Policy", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_message_type_has_no_contract()
    {
        Assert.Throws<ArgumentException>(() => MessageContractRules.For(typeof(UnknownMessage)));
    }

    private static void AssertEndpoints<TMessage>(
        EndpointVariantRule from,
        params EndpointVariantRule[] to)
        where TMessage : OrgMessage =>
        AssertEndpoints<TMessage>(
            ImmutableArray.Create(from),
            ImmutableArray.CreateRange(to));

    private static void AssertEndpoints<TMessage>(
        ImmutableArray<EndpointVariantRule> from,
        ImmutableArray<EndpointVariantRule> to)
        where TMessage : OrgMessage
    {
        var contract = MessageContractRules.For<TMessage>();

        Assert.Equal(from, contract.From);
        Assert.Equal(to, contract.To);
    }

    private static EndpointVariantRule Position() =>
        new(typeof(PositionEndpointRef));

    private static EndpointVariantRule Owner() =>
        new(typeof(OrganizationOwnerEndpointRef));

    private static EndpointVariantRule System(SystemEndpointKind kind) =>
        new(typeof(SystemEndpointRef), kind);

    private sealed record UnknownMessage : OrgMessage
    {
        public UnknownMessage()
            : base(null!, null!, null!, null!, null!, default, 0, default, null)
        {
        }

        public override MessageChannel Channel => MessageChannel.System;
    }
}
