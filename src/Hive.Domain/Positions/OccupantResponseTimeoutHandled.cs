using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.Positions;

/// <summary>
/// Durable, idempotent result of applying the response-timeout policy to one notified message.
/// Exactly one of a validated escalation or a terminal operational alert is recorded.
/// </summary>
public sealed record OccupantResponseTimeoutHandled : PositionEvent
{
    public OccupantResponseTimeoutHandled(
        MessageId message,
        ThreadId thread,
        OccupantId occupant,
        UserId user,
        OccupantChannelBindingId? binding,
        DateTimeOffset scheduledFor,
        DateTimeOffset occurredAt,
        Escalation? escalation = null,
        bool operationalAlert = false,
        bool killSwitchRequested = false)
        : base(occurredAt)
    {
        if ((escalation is null) == !operationalAlert)
        {
            throw new ArgumentException(
                "A response timeout must record either an escalation or a terminal operational alert.",
                nameof(escalation));
        }

        if (killSwitchRequested && !operationalAlert)
        {
            throw new ArgumentException(
                "A kill-switch request is only valid for a terminal operational alert.",
                nameof(killSwitchRequested));
        }

        Message = message ?? throw new ArgumentNullException(nameof(message));
        Thread = thread ?? throw new ArgumentNullException(nameof(thread));
        Occupant = occupant ?? throw new ArgumentNullException(nameof(occupant));
        User = user ?? throw new ArgumentNullException(nameof(user));
        Binding = binding;
        ScheduledFor = scheduledFor;
        Escalation = escalation;
        OperationalAlert = operationalAlert;
        KillSwitchRequested = killSwitchRequested;

        if (Escalation is not null
            && (Escalation.Id == Message || Escalation.Thread != Thread))
        {
            throw new ArgumentException(
                "A timeout escalation must use a distinct message id and preserve the source thread.",
                nameof(escalation));
        }
    }

    public MessageId Message { get; }

    public ThreadId Thread { get; }

    public OccupantId Occupant { get; }

    public UserId User { get; }

    public OccupantChannelBindingId? Binding { get; }

    public DateTimeOffset ScheduledFor { get; }

    public Escalation? Escalation { get; }

    public bool OperationalAlert { get; }

    public bool KillSwitchRequested { get; }
}
