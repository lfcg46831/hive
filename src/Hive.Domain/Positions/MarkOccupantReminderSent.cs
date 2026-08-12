using Hive.Domain.Identity;

namespace Hive.Domain.Positions;

/// <summary>
/// Records that a previously scheduled reminder was sent through a specific opaque binding.
/// Repeating the same message/reminder pair is an idempotent no-op.
/// </summary>
public sealed record MarkOccupantReminderSent : PositionCommand
{
    public MarkOccupantReminderSent(
        MessageId message,
        OccupantReminderId reminder,
        OccupantChannelBindingId binding)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Reminder = reminder ?? throw new ArgumentNullException(nameof(reminder));
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
    }

    public MessageId Message { get; }

    public OccupantReminderId Reminder { get; }

    public OccupantChannelBindingId Binding { get; }
}
