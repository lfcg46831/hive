using System.Collections.Immutable;

namespace Hive.Domain.Messaging;

public sealed record EndpointVariantRule
{
    public EndpointVariantRule(Type endpointType, SystemEndpointKind? systemKind = null)
    {
        ArgumentNullException.ThrowIfNull(endpointType);

        if (endpointType != typeof(PositionEndpointRef)
            && endpointType != typeof(OrganizationOwnerEndpointRef)
            && endpointType != typeof(SystemEndpointRef))
        {
            throw new ArgumentException(
                "Endpoint type must be a canonical EndpointRef variant.",
                nameof(endpointType));
        }

        if (endpointType == typeof(SystemEndpointRef))
        {
            if (systemKind is null || !Enum.IsDefined(systemKind.Value))
            {
                throw new ArgumentException(
                    "A system endpoint rule requires a defined system endpoint kind.",
                    nameof(systemKind));
            }
        }
        else if (systemKind is not null)
        {
            throw new ArgumentException(
                "Only a system endpoint rule can specify a system endpoint kind.",
                nameof(systemKind));
        }

        EndpointType = endpointType;
        SystemKind = systemKind;
    }

    public Type EndpointType { get; }

    public SystemEndpointKind? SystemKind { get; }
}

public sealed record MessageReferenceRule(
    string SourceProperty,
    Type TargetMessageType,
    string TargetProperty,
    bool IsRequired,
    bool MustShareOrganization,
    bool MustShareThread,
    bool DisallowSelfReference,
    bool DisallowCycles);

public sealed record MessageContractRule(
    Type MessageType,
    MessageChannel Channel,
    ImmutableArray<string> RequiredFields,
    ImmutableArray<string> OptionalFields,
    ImmutableArray<EndpointVariantRule> From,
    ImmutableArray<EndpointVariantRule> To,
    ImmutableArray<MessageReferenceRule> References);

public static class MessageContractRules
{
    private static readonly ImmutableArray<string> CommonRequiredFields =
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

    private static readonly EndpointVariantRule Position =
        new(typeof(PositionEndpointRef));

    private static readonly EndpointVariantRule Owner =
        new(typeof(OrganizationOwnerEndpointRef));

    private static readonly EndpointVariantRule Scheduler =
        new(typeof(SystemEndpointRef), SystemEndpointKind.Scheduler);

    private static readonly EndpointVariantRule DomainEvents =
        new(typeof(SystemEndpointRef), SystemEndpointKind.DomainEvents);

    public static ImmutableDictionary<Type, MessageContractRule> All { get; } =
        new[]
        {
            Rule<Directive>(
                MessageChannel.Vertical,
                [nameof(Directive.DirectiveId), nameof(Directive.Objective), nameof(Directive.Context)],
                [nameof(OrgMessage.Deadline), nameof(Directive.ParentDirectiveId)],
                [Position],
                [Position],
                [new(
                    nameof(Directive.ParentDirectiveId),
                    typeof(Directive),
                    nameof(Directive.DirectiveId),
                    IsRequired: false,
                    MustShareOrganization: true,
                    MustShareThread: true,
                    DisallowSelfReference: true,
                    DisallowCycles: true)]),
            Rule<Report>(
                MessageChannel.Vertical,
                [nameof(Report.AboutDirectiveId), nameof(Report.Kind), nameof(Report.Body)],
                [nameof(OrgMessage.Deadline)],
                [Position],
                [Position],
                [new(
                    nameof(Report.AboutDirectiveId),
                    typeof(Directive),
                    nameof(Directive.DirectiveId),
                    IsRequired: true,
                    MustShareOrganization: true,
                    MustShareThread: true,
                    DisallowSelfReference: false,
                    DisallowCycles: false)]),
            Rule<Escalation>(
                MessageChannel.Vertical,
                [nameof(Escalation.Issue), nameof(Escalation.Context), nameof(Escalation.OptionsConsidered)],
                [nameof(OrgMessage.Deadline)],
                [Position],
                [Position, Owner]),
            Rule<Memo>(
                MessageChannel.Horizontal,
                [nameof(Memo.Body)],
                [nameof(OrgMessage.Deadline)],
                [Position],
                [Position]),
            Rule<PeerRequest>(
                MessageChannel.Horizontal,
                [nameof(PeerRequest.Ask)],
                [nameof(OrgMessage.Deadline)],
                [Position],
                [Position]),
            Rule<PeerResponse>(
                MessageChannel.Horizontal,
                [nameof(PeerResponse.InReplyTo), nameof(PeerResponse.Body)],
                [nameof(OrgMessage.Deadline)],
                [Position],
                [Position]),
            Rule<ApprovalRequest>(
                MessageChannel.Governance,
                [
                    nameof(ApprovalRequest.Action),
                    nameof(ApprovalRequest.Justification),
                    nameof(ApprovalRequest.Policy),
                ],
                [nameof(OrgMessage.Deadline)],
                [Position],
                [Position, Owner]),
            Rule<ApprovalDecision>(
                MessageChannel.Governance,
                [nameof(ApprovalDecision.RequestId), nameof(ApprovalDecision.Approved)],
                [nameof(OrgMessage.Deadline), nameof(ApprovalDecision.Reason)],
                [Position, Owner],
                [Position]),
            Rule<AuthorizationGrant>(
                MessageChannel.Governance,
                [
                    nameof(AuthorizationGrant.InReplyTo),
                    nameof(AuthorizationGrant.RetainedActionId),
                    nameof(AuthorizationGrant.Fingerprint),
                    nameof(AuthorizationGrant.Key),
                    nameof(AuthorizationGrant.ExpiresAt),
                ],
                [nameof(OrgMessage.Deadline), nameof(AuthorizationGrant.Reason)],
                [Position, Owner],
                [Position]),
            Rule<AuthorizationDenial>(
                MessageChannel.Governance,
                [
                    nameof(AuthorizationDenial.InReplyTo),
                    nameof(AuthorizationDenial.RetainedActionId),
                    nameof(AuthorizationDenial.Reason),
                ],
                [nameof(OrgMessage.Deadline)],
                [Position, Owner],
                [Position]),
            Rule<Pulse>(
                MessageChannel.System,
                [nameof(Pulse.ScheduleId), nameof(Pulse.Payload)],
                [nameof(OrgMessage.Deadline)],
                [Scheduler],
                [Position]),
            Rule<EventTrigger>(
                MessageChannel.System,
                [nameof(EventTrigger.EventType), nameof(EventTrigger.Payload)],
                [nameof(OrgMessage.Deadline)],
                [DomainEvents],
                [Position]),
        }.ToImmutableDictionary(contract => contract.MessageType);

    public static MessageContractRule For<TMessage>()
        where TMessage : OrgMessage =>
        For(typeof(TMessage));

    public static MessageContractRule For(Type messageType)
    {
        ArgumentNullException.ThrowIfNull(messageType);

        if (All.TryGetValue(messageType, out var contract))
        {
            return contract;
        }

        throw new ArgumentException(
            $"{messageType.Name} is not a canonical organizational message type.",
            nameof(messageType));
    }

    private static MessageContractRule Rule<TMessage>(
        MessageChannel channel,
        ImmutableArray<string> payloadRequiredFields,
        ImmutableArray<string> optionalFields,
        ImmutableArray<EndpointVariantRule> from,
        ImmutableArray<EndpointVariantRule> to,
        ImmutableArray<MessageReferenceRule> references = default)
        where TMessage : OrgMessage =>
        new(
            typeof(TMessage),
            channel,
            [.. CommonRequiredFields, .. payloadRequiredFields],
            optionalFields,
            from,
            to,
            references.IsDefault ? [] : references);
}
