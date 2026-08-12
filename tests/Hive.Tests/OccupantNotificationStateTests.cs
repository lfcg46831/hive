using Hive.Domain.Identity;
using Hive.Domain.OccupantChannels;
using Hive.Domain.Positions;

namespace Hive.Tests;

public sealed class OccupantNotificationStateTests
{
    private static readonly DateTimeOffset At =
        new(2026, 8, 11, 18, 0, 0, TimeSpan.Zero);
    private static readonly MessageId Message = MessageId.From(
        Guid.Parse("30303030-1111-2222-3333-444444444444"));
    private static readonly ThreadId Thread = ThreadId.From(
        Guid.Parse("40404040-5555-6666-7777-888888888888"));
    private static readonly OccupantId Occupant = OccupantId.From("human:delivery-lead");
    private static readonly UserId User = UserId.From(
        Guid.Parse("50505050-aaaa-bbbb-cccc-dddddddddddd"));
    private static readonly OccupantChannelBindingId Binding = OccupantChannelBindingId.From(
        Guid.Parse("60606060-aaaa-bbbb-cccc-dddddddddddd"));
    private static readonly OccupantReminderId Reminder = OccupantReminderId.From(
        Guid.Parse("70707070-aaaa-bbbb-cccc-dddddddddddd"));

    [Fact]
    public void Delivery_and_reminder_facts_fold_idempotently_by_message_and_reminder()
    {
        var requested = Requested();
        var confirmed = new OccupantChannelDeliveryConfirmed(
            Message,
            Thread,
            Occupant,
            User,
            Binding,
            At.AddMinutes(1));
        var scheduled = new OccupantReminderScheduled(
            Message,
            Thread,
            Occupant,
            User,
            Binding,
            Reminder,
            At.AddHours(1),
            At.AddMinutes(2));
        var sent = new OccupantReminderSent(
            Message,
            Thread,
            Occupant,
            User,
            Binding,
            Reminder,
            At.AddMinutes(3));

        var state = PositionState.Empty
            .Apply(requested)
            .Apply(requested)
            .Apply(confirmed)
            .Apply(confirmed)
            .Apply(scheduled)
            .Apply(scheduled)
            .Apply(sent)
            .Apply(sent);

        var notification = Assert.Single(state.OccupantNotifications).Value;
        Assert.Equal(OccupantNotificationDeliveryStatus.Confirmed, notification.Status);
        Assert.Equal(At.AddMinutes(1), notification.CompletedAt);
        var reminder = Assert.Single(notification.Reminders);
        Assert.Equal(At.AddHours(1), reminder.ScheduledFor);
        Assert.Equal(At.AddMinutes(3), reminder.SentAt);
    }

    [Fact]
    public void Notification_state_round_trips_through_position_snapshot()
    {
        var failed = new OccupantChannelDeliveryFailed(
            Message,
            Thread,
            Occupant,
            User,
            Binding,
            new OccupantChannelDeliveryError(
                OccupantChannelDeliveryErrorCode.ChannelUnavailable,
                isRetryable: true),
            At.AddMinutes(1));
        var state = PositionState.Empty.Apply(Requested()).Apply(failed);

        var restored = PositionState.Restore(state.ToSnapshot(At.AddMinutes(2)));

        Assert.Single(restored.OccupantNotifications);
        Assert.Equal(
            state.OccupantNotifications[Message],
            restored.OccupantNotifications[Message]);
        var notification = restored.OccupantNotifications[Message];
        Assert.Equal(OccupantNotificationDeliveryStatus.Failed, notification.Status);
        Assert.Equal(OccupantChannelDeliveryErrorCode.ChannelUnavailable, notification.Failure!.Code);
        Assert.True(notification.Failure.IsRetryable);
    }

    private static OccupantChannelDeliveryRequested Requested() => new(
        Message,
        Thread,
        Occupant,
        User,
        Binding,
        At);
}
