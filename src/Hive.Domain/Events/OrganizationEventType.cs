namespace Hive.Domain.Events;

/// <summary>The closed F1 organizational event catalog (US-F1-07-T01).</summary>
public enum OrganizationEventType
{
    DirectiveDeadlineApproaching = 1,
    PositionBlockedProlonged = 2,
    BudgetThresholdReached = 3,
}

public static class OrganizationEventTypeContract
{
    public static OrganizationEventType RequireDefined(OrganizationEventType value, string parameterName) =>
        Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(parameterName, value, "Unknown organizational event type.");

    public static string ToWireValue(OrganizationEventType value) => value switch
    {
        OrganizationEventType.DirectiveDeadlineApproaching => "directive-deadline-approaching",
        OrganizationEventType.PositionBlockedProlonged => "position-blocked-prolonged",
        OrganizationEventType.BudgetThresholdReached => "budget-threshold-reached",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown organizational event type."),
    };

    public static OrganizationEventType ParseWireValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return TryParseWireValue(value, out var type)
            ? type
            : throw new ArgumentException("Unknown organizational event type.", nameof(value));
    }

    public static bool TryParseWireValue(string? value, out OrganizationEventType type)
    {
        type = value switch
        {
            "directive-deadline-approaching" => OrganizationEventType.DirectiveDeadlineApproaching,
            "position-blocked-prolonged" => OrganizationEventType.PositionBlockedProlonged,
            "budget-threshold-reached" => OrganizationEventType.BudgetThresholdReached,
            _ => default,
        };
        return type != default;
    }
}
