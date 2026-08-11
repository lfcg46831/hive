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
    {
        OrganizationId = organizationId ?? throw new ArgumentNullException(nameof(organizationId));
        PositionId = positionId ?? throw new ArgumentNullException(nameof(positionId));
        OccupantId = occupantId ?? throw new ArgumentNullException(nameof(occupantId));
        UserId = userId ?? throw new ArgumentNullException(nameof(userId));
        OccupantChannelBindingId = occupantChannelBindingId;
        Message = message ?? throw new ArgumentNullException(nameof(message));

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
}
