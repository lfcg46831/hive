using Hive.Domain.Identity;

namespace Hive.Domain.Positions;

/// <summary>
/// Requests durable scheduling of one reminder for a message already notified through an occupant
/// channel. Repeating the same message/reminder pair is an idempotent no-op.
/// </summary>
public sealed record ScheduleOccupantReminder : PositionCommand
{
    public ScheduleOccupantReminder(
        MessageId message,
        OccupantReminderId reminder,
        DateTimeOffset scheduledFor)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Reminder = reminder ?? throw new ArgumentNullException(nameof(reminder));
        ScheduledFor = scheduledFor;
    }

    public MessageId Message { get; }

    public OccupantReminderId Reminder { get; }

    public DateTimeOffset ScheduledFor { get; }
}
