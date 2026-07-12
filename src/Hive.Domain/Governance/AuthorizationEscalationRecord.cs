using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.Governance;

/// <summary>
/// Routing facts fixed when a gate escalation is accepted, plus the authorization response already
/// accepted for that escalation, if any (US-F0-12-T04).
/// </summary>
public sealed record AuthorizationEscalationRecord
{
    public AuthorizationEscalationRecord(
        MessageId escalationId,
        OrganizationId organizationId,
        ThreadId thread,
        PositionEndpointRef requester,
        EndpointRef recipient,
        RetainedActionId retainedActionId,
        MessageId? resolutionMessageId = null)
    {
        ArgumentNullException.ThrowIfNull(escalationId);
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(requester);
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(retainedActionId);

        if (recipient is not PositionEndpointRef and not OrganizationOwnerEndpointRef)
        {
            throw new ArgumentException(
                "An authorization escalation recipient must be a position or the organization owner.",
                nameof(recipient));
        }

        EscalationId = escalationId;
        OrganizationId = organizationId;
        Thread = thread;
        Requester = requester;
        Recipient = recipient;
        RetainedActionId = retainedActionId;
        ResolutionMessageId = resolutionMessageId;
    }

    public MessageId EscalationId { get; }

    public OrganizationId OrganizationId { get; }

    public ThreadId Thread { get; }

    public PositionEndpointRef Requester { get; }

    public EndpointRef Recipient { get; }

    public RetainedActionId RetainedActionId { get; }

    public MessageId? ResolutionMessageId { get; }

    public bool IsResolved => ResolutionMessageId is not null;
}
