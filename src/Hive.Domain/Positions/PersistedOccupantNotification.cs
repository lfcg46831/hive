using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Hive.Domain.Identity;
using Hive.Domain.OccupantChannels;

namespace Hive.Domain.Positions;

public enum OccupantNotificationDeliveryStatus
{
    Requested = 1,
    Confirmed = 2,
    Failed = 3,
}

public static class OccupantNotificationDeliveryStatusContract
{
    public static OccupantNotificationDeliveryStatus RequireDefined(
        OccupantNotificationDeliveryStatus value,
        string parameterName) => value switch
        {
            OccupantNotificationDeliveryStatus.Requested or
            OccupantNotificationDeliveryStatus.Confirmed or
            OccupantNotificationDeliveryStatus.Failed => value,
            _ => throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Occupant notification delivery status is undefined."),
        };

    public static string ToWireValue(OccupantNotificationDeliveryStatus value) =>
        RequireDefined(value, nameof(value)) switch
        {
            OccupantNotificationDeliveryStatus.Requested => "requested",
            OccupantNotificationDeliveryStatus.Confirmed => "confirmed",
            OccupantNotificationDeliveryStatus.Failed => "failed",
            _ => throw new InvalidOperationException("Validated delivery status is not mapped."),
        };

    public static bool TryParseWireValue(
        string? value,
        out OccupantNotificationDeliveryStatus result)
    {
        switch (value)
        {
            case "requested":
                result = OccupantNotificationDeliveryStatus.Requested;
                return true;
            case "confirmed":
                result = OccupantNotificationDeliveryStatus.Confirmed;
                return true;
            case "failed":
                result = OccupantNotificationDeliveryStatus.Failed;
                return true;
            default:
                result = default;
                return false;
        }
    }
}

/// <summary>Recoverable state of one reminder belonging to an occupant notification.</summary>
public sealed record PersistedOccupantReminder
{
    public PersistedOccupantReminder(
        OccupantReminderId id,
        DateTimeOffset scheduledFor,
        DateTimeOffset scheduledAt,
        OccupantChannelBindingId? scheduledBinding,
        DateTimeOffset? sentAt = null,
        OccupantChannelBindingId? sentBinding = null)
    {
        if (sentAt is null != sentBinding is null)
        {
            throw new ArgumentException(
                "A sent reminder must have both a sent instant and binding.",
                nameof(sentAt));
        }

        Id = id ?? throw new ArgumentNullException(nameof(id));
        ScheduledFor = scheduledFor;
        ScheduledAt = scheduledAt;
        ScheduledBinding = scheduledBinding;
        SentAt = sentAt;
        SentBinding = sentBinding;
    }

    public OccupantReminderId Id { get; }

    public DateTimeOffset ScheduledFor { get; }

    public DateTimeOffset ScheduledAt { get; }

    public OccupantChannelBindingId? ScheduledBinding { get; }

    public DateTimeOffset? SentAt { get; }

    public OccupantChannelBindingId? SentBinding { get; }

    public PersistedOccupantReminder MarkSent(
        OccupantChannelBindingId binding,
        DateTimeOffset sentAt) =>
        SentAt is null
            ? new PersistedOccupantReminder(
                Id,
                ScheduledFor,
                ScheduledAt,
                ScheduledBinding,
                sentAt,
                binding ?? throw new ArgumentNullException(nameof(binding)))
            : this;
}

/// <summary>
/// Recoverable per-message state for occupant-channel delivery and its scheduled reminders. It
/// contains only internal identities and opaque binding references, never a personal endpoint.
/// </summary>
public sealed record PersistedOccupantNotification
{
    public PersistedOccupantNotification(
        MessageId message,
        ThreadId thread,
        OccupantId occupant,
        UserId user,
        OccupantChannelBindingId? binding,
        OccupantNotificationDeliveryStatus status,
        DateTimeOffset requestedAt,
        DateTimeOffset? completedAt = null,
        OccupantChannelDeliveryError? failure = null,
        ImmutableArray<PersistedOccupantReminder> reminders = default,
        OccupantResponseTimeoutHandled? responseTimeout = null)
    {
        status = OccupantNotificationDeliveryStatusContract.RequireDefined(status, nameof(status));
        if (status == OccupantNotificationDeliveryStatus.Requested &&
            (completedAt is not null || failure is not null))
        {
            throw new ArgumentException(
                "A requested notification cannot have terminal delivery data.",
                nameof(status));
        }

        if (status == OccupantNotificationDeliveryStatus.Confirmed &&
            (completedAt is null || failure is not null || binding is null))
        {
            throw new ArgumentException(
                "A confirmed notification requires a binding and completion instant only.",
                nameof(status));
        }

        if (status == OccupantNotificationDeliveryStatus.Failed &&
            (completedAt is null || failure is null))
        {
            throw new ArgumentException(
                "A failed notification requires a completion instant and structured error.",
                nameof(status));
        }

        var reminderArray = reminders.IsDefault
            ? ImmutableArray<PersistedOccupantReminder>.Empty
            : reminders;
        if (reminderArray.Any(item => item is null) ||
            reminderArray.Select(item => item.Id).Distinct().Count() != reminderArray.Length)
        {
            throw new ArgumentException(
                "Notification reminders must be non-null and have unique ids.",
                nameof(reminders));
        }

        Message = message ?? throw new ArgumentNullException(nameof(message));
        Thread = thread ?? throw new ArgumentNullException(nameof(thread));
        Occupant = occupant ?? throw new ArgumentNullException(nameof(occupant));
        User = user ?? throw new ArgumentNullException(nameof(user));
        Binding = binding;
        Status = status;
        RequestedAt = requestedAt;
        CompletedAt = completedAt;
        Failure = failure;
        Reminders = reminderArray;
        if (responseTimeout is not null
            && (responseTimeout.Message != Message
                || responseTimeout.Thread != Thread
                || responseTimeout.Occupant != Occupant
                || responseTimeout.User != User))
        {
            throw new ArgumentException(
                "Response timeout state must match its occupant notification.",
                nameof(responseTimeout));
        }

        ResponseTimeout = responseTimeout;
    }

    public MessageId Message { get; }

    public ThreadId Thread { get; }

    public OccupantId Occupant { get; }

    public UserId User { get; }

    public OccupantChannelBindingId? Binding { get; }

    public OccupantNotificationDeliveryStatus Status { get; }

    public DateTimeOffset RequestedAt { get; }

    public DateTimeOffset? CompletedAt { get; }

    public OccupantChannelDeliveryError? Failure { get; }

    public ImmutableArray<PersistedOccupantReminder> Reminders { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OccupantResponseTimeoutHandled? ResponseTimeout { get; }

    public static PersistedOccupantNotification Requested(
        OccupantChannelDeliveryRequested requested) =>
        new(
            requested.Message,
            requested.Thread,
            requested.Occupant,
            requested.User,
            requested.Binding,
            OccupantNotificationDeliveryStatus.Requested,
            requested.OccurredAt);

    public PersistedOccupantNotification Confirm(OccupantChannelDeliveryConfirmed confirmed) =>
        Status == OccupantNotificationDeliveryStatus.Requested && Matches(confirmed)
            ? new PersistedOccupantNotification(
                Message,
                Thread,
                Occupant,
                User,
                confirmed.Binding,
                OccupantNotificationDeliveryStatus.Confirmed,
                RequestedAt,
                confirmed.OccurredAt,
                reminders: Reminders,
                responseTimeout: ResponseTimeout)
            : this;

    public PersistedOccupantNotification Fail(OccupantChannelDeliveryFailed failed) =>
        Status == OccupantNotificationDeliveryStatus.Requested && Matches(failed)
            ? new PersistedOccupantNotification(
                Message,
                Thread,
                Occupant,
                User,
                failed.Binding,
                OccupantNotificationDeliveryStatus.Failed,
                RequestedAt,
                failed.OccurredAt,
                failed.Error,
                Reminders,
                ResponseTimeout)
            : this;

    public PersistedOccupantNotification Schedule(OccupantReminderScheduled scheduled)
    {
        if (!Matches(scheduled) || Reminders.Any(item => item.Id == scheduled.Reminder))
        {
            return this;
        }

        return Copy(Reminders.Add(new PersistedOccupantReminder(
            scheduled.Reminder,
            scheduled.ScheduledFor,
            scheduled.OccurredAt,
            scheduled.Binding)));
    }

    public PersistedOccupantNotification MarkSent(OccupantReminderSent sent)
    {
        if (!Matches(sent))
        {
            return this;
        }

        for (var index = 0; index < Reminders.Length; index++)
        {
            if (Reminders[index].Id == sent.Reminder)
            {
                return Copy(Reminders.SetItem(
                    index,
                    Reminders[index].MarkSent(sent.Binding, sent.OccurredAt)));
            }
        }

        return this;
    }

    public PersistedOccupantNotification HandleTimeout(OccupantResponseTimeoutHandled handled) =>
        ResponseTimeout is null && Matches(handled)
            ? Copy(Reminders, handled)
            : this;

    private bool Matches(OccupantChannelDeliveryConfirmed @event) =>
        @event.Message == Message && @event.Thread == Thread &&
        @event.Occupant == Occupant && @event.User == User && @event.Binding == Binding;

    private bool Matches(OccupantChannelDeliveryFailed @event) =>
        @event.Message == Message && @event.Thread == Thread &&
        @event.Occupant == Occupant && @event.User == User && @event.Binding == Binding;

    private bool Matches(OccupantReminderScheduled @event) =>
        @event.Message == Message && @event.Thread == Thread &&
        @event.Occupant == Occupant && @event.User == User && @event.Binding == Binding;

    private bool Matches(OccupantReminderSent @event) =>
        @event.Message == Message && @event.Thread == Thread &&
        @event.Occupant == Occupant && @event.User == User;

    private bool Matches(OccupantResponseTimeoutHandled @event) =>
        @event.Message == Message && @event.Thread == Thread &&
        @event.Occupant == Occupant && @event.User == User;

    private PersistedOccupantNotification Copy(
        ImmutableArray<PersistedOccupantReminder> reminders,
        OccupantResponseTimeoutHandled? responseTimeout = null) =>
        new(
            Message,
            Thread,
            Occupant,
            User,
            Binding,
            Status,
            RequestedAt,
            CompletedAt,
            Failure,
            reminders,
            responseTimeout ?? ResponseTimeout);
}
