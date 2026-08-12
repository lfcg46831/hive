using Hive.Domain.Identity;
using Hive.Domain.OccupantChannels;

namespace Hive.Domain.Positions;

/// <summary>An occupant-channel notification failed with a transport-neutral reason.</summary>
public sealed record OccupantChannelDeliveryFailed : PositionEvent
{
    public OccupantChannelDeliveryFailed(
        MessageId message,
        ThreadId thread,
        OccupantId occupant,
        UserId user,
        OccupantChannelBindingId? binding,
        OccupantChannelDeliveryError error,
        DateTimeOffset occurredAt)
        : base(occurredAt)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Thread = thread ?? throw new ArgumentNullException(nameof(thread));
        Occupant = occupant ?? throw new ArgumentNullException(nameof(occupant));
        User = user ?? throw new ArgumentNullException(nameof(user));
        Binding = binding;
        Error = error ?? throw new ArgumentNullException(nameof(error));
    }

    public MessageId Message { get; }

    public ThreadId Thread { get; }

    public OccupantId Occupant { get; }

    public UserId User { get; }

    public OccupantChannelBindingId? Binding { get; }

    public OccupantChannelDeliveryError Error { get; }
}
