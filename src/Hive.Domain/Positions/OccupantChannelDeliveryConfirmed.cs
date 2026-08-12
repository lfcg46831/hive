using Hive.Domain.Identity;

namespace Hive.Domain.Positions;

/// <summary>An occupant-channel notification was confirmed by the channel adapter.</summary>
public sealed record OccupantChannelDeliveryConfirmed : PositionEvent
{
    public OccupantChannelDeliveryConfirmed(
        MessageId message,
        ThreadId thread,
        OccupantId occupant,
        UserId user,
        OccupantChannelBindingId binding,
        DateTimeOffset occurredAt)
        : base(occurredAt)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Thread = thread ?? throw new ArgumentNullException(nameof(thread));
        Occupant = occupant ?? throw new ArgumentNullException(nameof(occupant));
        User = user ?? throw new ArgumentNullException(nameof(user));
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
    }

    public MessageId Message { get; }

    public ThreadId Thread { get; }

    public OccupantId Occupant { get; }

    public UserId User { get; }

    public OccupantChannelBindingId Binding { get; }
}
