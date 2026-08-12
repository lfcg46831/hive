using Hive.Domain.Identity;

namespace Hive.Domain.Positions;

/// <summary>
/// A human-position notification was durably admitted for delivery through an occupant channel.
/// The binding reference is opaque and may be absent when delivery must fail closed.
/// </summary>
public sealed record OccupantChannelDeliveryRequested : PositionEvent
{
    public OccupantChannelDeliveryRequested(
        MessageId message,
        ThreadId thread,
        OccupantId occupant,
        UserId user,
        OccupantChannelBindingId? binding,
        DateTimeOffset occurredAt)
        : base(occurredAt)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Thread = thread ?? throw new ArgumentNullException(nameof(thread));
        Occupant = occupant ?? throw new ArgumentNullException(nameof(occupant));
        User = user ?? throw new ArgumentNullException(nameof(user));
        Binding = binding;
    }

    public MessageId Message { get; }

    public ThreadId Thread { get; }

    public OccupantId Occupant { get; }

    public UserId User { get; }

    public OccupantChannelBindingId? Binding { get; }
}
