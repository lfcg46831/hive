using Hive.Domain.Identity;

namespace Hive.Domain.Events;

/// <summary>Source correlation, independent of the future trigger's message and thread identities.</summary>
public sealed record EventSourceCorrelation
{
    public EventSourceCorrelation(MessageId messageId, ThreadId threadId, DirectiveId? directiveId = null)
    {
        ArgumentNullException.ThrowIfNull(messageId);
        ArgumentNullException.ThrowIfNull(threadId);
        MessageId = messageId;
        ThreadId = threadId;
        DirectiveId = directiveId;
    }

    public MessageId MessageId { get; }
    public ThreadId ThreadId { get; }
    public DirectiveId? DirectiveId { get; }
}

public enum PositionBlockedCause
{
    PendingEscalation = 1,
    ConfigurationBlocked = 2,
}

public static class PositionBlockedCauseContract
{
    public static string ToWireValue(PositionBlockedCause value) => value switch
    {
        PositionBlockedCause.PendingEscalation => "pending-escalation",
        PositionBlockedCause.ConfigurationBlocked => "configuration-blocked",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown blocked cause."),
    };

    public static PositionBlockedCause ParseWireValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value switch
        {
            "pending-escalation" => PositionBlockedCause.PendingEscalation,
            "configuration-blocked" => PositionBlockedCause.ConfigurationBlocked,
            _ => throw new ArgumentException("Unknown blocked cause.", nameof(value)),
        };
    }
}

/// <summary>Daily EUR caps from occupant.ai.budget; hourly call limits are not percentage budgets.</summary>
public enum DailyBudgetKind
{
    Reactive = 1,
    Proactive = 2,
    Total = 3,
}

public static class DailyBudgetKindContract
{
    public static string ToWireValue(DailyBudgetKind value) => value switch
    {
        DailyBudgetKind.Reactive => "reactive",
        DailyBudgetKind.Proactive => "proactive",
        DailyBudgetKind.Total => "total",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown daily budget."),
    };

    public static DailyBudgetKind ParseWireValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value switch
        {
            "reactive" => DailyBudgetKind.Reactive,
            "proactive" => DailyBudgetKind.Proactive,
            "total" => DailyBudgetKind.Total,
            _ => throw new ArgumentException("Unknown daily budget.", nameof(value)),
        };
    }
}
