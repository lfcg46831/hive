using Hive.Domain.Messaging;

namespace Hive.Domain.Positions;

/// <summary>Durable notification at the recipient or durable escalation outbox at the requester.</summary>
public sealed record PeerRejectionEscalationUpdated : PositionEvent
{
    public PeerRejectionEscalationUpdated(RecordPeerRequestRejection rejection, Escalation? escalation,
        bool completed, DateTimeOffset occurredAt) : base(occurredAt)
    {
        ArgumentNullException.ThrowIfNull(rejection);
        if (escalation is not null && (escalation.Id != rejection.EscalationId
            || escalation.OrganizationId != rejection.Request.OrganizationId
            || escalation.From != rejection.Request.From || escalation.Thread != rejection.Request.Thread))
            throw new ArgumentException("Escalation must match the rejection identity.", nameof(escalation));
        Rejection = rejection;
        Escalation = escalation;
        Completed = completed;
    }

    public RecordPeerRequestRejection Rejection { get; }
    public Escalation? Escalation { get; }
    public bool Completed { get; }
}
