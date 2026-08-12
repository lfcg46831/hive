using Hive.Domain.Identity;

namespace Hive.Domain.Positions;

/// <summary>An occupant-channel reminder was sent through the recorded opaque binding.</summary>
public sealed record OccupantReminderSent : PositionEvent
{
    public OccupantReminderSent(
        MessageId message,
        ThreadId thread,
        OccupantId occupant,
        UserId user,
        OccupantChannelBindingId binding,
        OccupantReminderId reminder,
        DateTimeOffset occurredAt)
        : base(occurredAt)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Thread = thread ?? throw new ArgumentNullException(nameof(thread));
        Occupant = occupant ?? throw new ArgumentNullException(nameof(occupant));
        User = user ?? throw new ArgumentNullException(nameof(user));
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        Reminder = reminder ?? throw new ArgumentNullException(nameof(reminder));
    }

    public MessageId Message { get; }

    public ThreadId Thread { get; }

    public OccupantId Occupant { get; }

    public UserId User { get; }

    public OccupantChannelBindingId Binding { get; }

    public OccupantReminderId Reminder { get; }
}
