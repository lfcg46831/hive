using Hive.Domain.Identity;

namespace Hive.Domain.OccupantChannels;

/// <summary>
/// Immutable, channel-neutral notification request. It intentionally carries only the opaque
/// binding reference; a personal endpoint must never be copied into this contract.
/// </summary>
public sealed record OccupantChannelDeliveryRequest
{
    public OccupantChannelDeliveryRequest(
        OrganizationId organizationId,
        PositionId positionId,
        OccupantId occupantId,
        UserId userId,
        OccupantChannelBindingId occupantChannelBindingId,
        MessageId messageId,
        ThreadId threadId,
        string renderedMessage,
        OccupantChannelCorrelationToken correlationToken)
    {
        OrganizationId = organizationId ?? throw new ArgumentNullException(nameof(organizationId));
        PositionId = positionId ?? throw new ArgumentNullException(nameof(positionId));
        OccupantId = occupantId ?? throw new ArgumentNullException(nameof(occupantId));
        UserId = userId ?? throw new ArgumentNullException(nameof(userId));
        OccupantChannelBindingId = occupantChannelBindingId ??
            throw new ArgumentNullException(nameof(occupantChannelBindingId));
        MessageId = messageId ?? throw new ArgumentNullException(nameof(messageId));
        ThreadId = threadId ?? throw new ArgumentNullException(nameof(threadId));
        RenderedMessage = OccupantChannelContractGuards.RequireRenderedMessage(
            renderedMessage,
            nameof(renderedMessage));
        CorrelationToken = correlationToken ??
            throw new ArgumentNullException(nameof(correlationToken));
    }

    public OrganizationId OrganizationId { get; }

    public PositionId PositionId { get; }

    public OccupantId OccupantId { get; }

    public UserId UserId { get; }

    public OccupantChannelBindingId OccupantChannelBindingId { get; }

    public MessageId MessageId { get; }

    public ThreadId ThreadId { get; }

    public string RenderedMessage { get; }

    public OccupantChannelCorrelationToken CorrelationToken { get; }
}
