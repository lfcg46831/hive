using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.Positions;

/// <summary>
/// Durable result of the immediate-escalation action for one message received while the human
/// occupant is absent. Exactly one validated escalation or terminal operational alert is retained.
/// </summary>
public sealed record OccupantAbsenceEscalationHandled : PositionEvent
{
    public OccupantAbsenceEscalationHandled(
        MessageId message,
        ThreadId thread,
        OccupantId occupant,
        DateTimeOffset occurredAt,
        Escalation? escalation = null,
        bool operationalAlert = false,
        bool killSwitchRequested = false)
        : base(occurredAt)
    {
        if ((escalation is null) == !operationalAlert)
        {
            throw new ArgumentException(
                "An absence escalation must record either an escalation or a terminal operational alert.",
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
        Escalation = escalation;
        OperationalAlert = operationalAlert;
        KillSwitchRequested = killSwitchRequested;

        if (Escalation is not null
            && (Escalation.Id == Message || Escalation.Thread != Thread))
        {
            throw new ArgumentException(
                "An absence escalation must use a distinct message id and preserve the source thread.",
                nameof(escalation));
        }
    }

    public MessageId Message { get; }

    public ThreadId Thread { get; }

    public OccupantId Occupant { get; }

    public Escalation? Escalation { get; }

    public bool OperationalAlert { get; }

    public bool KillSwitchRequested { get; }
}
