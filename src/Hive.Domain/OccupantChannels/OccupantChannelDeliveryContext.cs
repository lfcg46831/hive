using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.OccupantChannels;

/// <summary>
/// Canonical input used to render an occupant-channel request after a position dispatch is durable.
/// It contains only organizational identities, opaque user/binding references, and the already
/// persisted message; no personal endpoint is projected into the actor boundary.
/// </summary>
public sealed record OccupantChannelDeliveryContext
{
    public OccupantChannelDeliveryContext(
        OrganizationId organizationId,
        PositionId positionId,
        OccupantId occupantId,
        UserId userId,
        OccupantChannelBindingId? occupantChannelBindingId,
        OrgMessage message)
        : this(
            organizationId,
            positionId,
            occupantId,
            userId,
            occupantChannelBindingId,
            message,
            correlationMessageId: null,
            correlationRequestId: null)
    {
    }

    public OccupantChannelDeliveryContext(
        OrganizationId organizationId,
        PositionId positionId,
        OccupantId occupantId,
        UserId userId,
        OccupantChannelBindingId? occupantChannelBindingId,
        OrgMessage message,
        MessageId? correlationMessageId,
        MessageId? correlationRequestId)
    {
        OrganizationId = organizationId ?? throw new ArgumentNullException(nameof(organizationId));
        PositionId = positionId ?? throw new ArgumentNullException(nameof(positionId));
        OccupantId = occupantId ?? throw new ArgumentNullException(nameof(occupantId));
        UserId = userId ?? throw new ArgumentNullException(nameof(userId));
        OccupantChannelBindingId = occupantChannelBindingId;
        Message = message ?? throw new ArgumentNullException(nameof(message));
        CorrelationMessageId = correlationMessageId;
        CorrelationRequestId = correlationRequestId;

        if (CorrelationRequestId is not null && CorrelationMessageId is null)
        {
            throw new ArgumentException(
                "A correlation request id requires an explicit correlation message id.",
                nameof(correlationRequestId));
        }

        if (Message.OrganizationId != OrganizationId)
        {
            throw new ArgumentException(
                "Occupant-channel message organization must match the delivery context.",
                nameof(message));
        }
    }

    public OrganizationId OrganizationId { get; }

    public PositionId PositionId { get; }

    public OccupantId OccupantId { get; }

    public UserId UserId { get; }

    public OccupantChannelBindingId? OccupantChannelBindingId { get; }

    public OrgMessage Message { get; }

    /// <summary>
    /// Optional original message identity used by a derived notification such as a reminder.
    /// The delivery itself retains the distinct id of <see cref="Message"/> for transport idempotency.
    /// </summary>
    public MessageId? CorrelationMessageId { get; }

    /// <summary>Optional original approval request identity for a derived reminder.</summary>
    public MessageId? CorrelationRequestId { get; }
}
