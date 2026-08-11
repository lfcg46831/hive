using Hive.Domain.Identity;
using Hive.Domain.OccupantChannels;

namespace Hive.Actors.Positions;

/// <summary>
/// Ephemeral command returned by a human proxy to its parent position. Durable delivery state is
/// intentionally owned by the PositionActor and introduced by US-F1-03-T03.
/// </summary>
internal sealed record PositionOccupantChannelDeliveryReported
{
    public PositionOccupantChannelDeliveryReported(
        MessageId messageId,
        ThreadId threadId,
        OccupantId occupantId,
        UserId userId,
        OccupantChannelBindingId? bindingId,
        OccupantChannelDeliveryResult result)
    {
        MessageId = messageId ?? throw new ArgumentNullException(nameof(messageId));
        ThreadId = threadId ?? throw new ArgumentNullException(nameof(threadId));
        OccupantId = occupantId ?? throw new ArgumentNullException(nameof(occupantId));
        UserId = userId ?? throw new ArgumentNullException(nameof(userId));
        BindingId = bindingId;
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    public MessageId MessageId { get; }

    public ThreadId ThreadId { get; }

    public OccupantId OccupantId { get; }

    public UserId UserId { get; }

    public OccupantChannelBindingId? BindingId { get; }

    public OccupantChannelDeliveryResult Result { get; }
}
