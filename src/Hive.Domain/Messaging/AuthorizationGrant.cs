using Hive.Domain.Governance;
using Hive.Domain.Identity;

namespace Hive.Domain.Messaging;

public sealed record AuthorizationGrant : OrgMessage
{
    public AuthorizationGrant(
        MessageId id,
        OrganizationId organizationId,
        EndpointRef from,
        EndpointRef to,
        ThreadId thread,
        Priority priority,
        int schemaVersion,
        DateTimeOffset sentAt,
        DateTimeOffset? deadline,
        MessageId inReplyTo,
        RetainedActionId retainedActionId,
        ActionFingerprint fingerprint,
        AuthorityKey key,
        DateTimeOffset expiresAt,
        string? reason)
        : base(id, organizationId, from, to, thread, priority, schemaVersion, sentAt, deadline)
    {
        ArgumentNullException.ThrowIfNull(inReplyTo);
        ArgumentNullException.ThrowIfNull(retainedActionId);
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(key);

        InReplyTo = inReplyTo;
        RetainedActionId = retainedActionId;
        Fingerprint = fingerprint;
        Key = key;
        ExpiresAt = expiresAt;
        Reason = reason;
    }

    public MessageId InReplyTo { get; }

    public RetainedActionId RetainedActionId { get; }

    public ActionFingerprint Fingerprint { get; }

    public AuthorityKey Key { get; }

    public DateTimeOffset ExpiresAt { get; }

    public string? Reason { get; }

    public override MessageChannel Channel => MessageChannel.Governance;
}
