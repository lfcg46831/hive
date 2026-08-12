using Hive.Domain.Identity;

namespace Hive.Domain.Positions;

/// <summary>An occupant-channel reminder was durably scheduled for a notified message.</summary>
public sealed record OccupantReminderScheduled : PositionEvent
{
    public OccupantReminderScheduled(
        MessageId message,
        ThreadId thread,
        OccupantId occupant,
        UserId user,
        OccupantChannelBindingId? binding,
        OccupantReminderId reminder,
        DateTimeOffset scheduledFor,
        DateTimeOffset occurredAt)
        : base(occurredAt)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Thread = thread ?? throw new ArgumentNullException(nameof(thread));
        Occupant = occupant ?? throw new ArgumentNullException(nameof(occupant));
        User = user ?? throw new ArgumentNullException(nameof(user));
        Binding = binding;
        Reminder = reminder ?? throw new ArgumentNullException(nameof(reminder));
        ScheduledFor = scheduledFor;
    }

    public MessageId Message { get; }

    public ThreadId Thread { get; }

    public OccupantId Occupant { get; }

    public UserId User { get; }

    public OccupantChannelBindingId? Binding { get; }

    public OccupantReminderId Reminder { get; }

    public DateTimeOffset ScheduledFor { get; }
}
