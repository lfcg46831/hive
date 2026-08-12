namespace Hive.Domain.Organization.Configuration;

/// <summary>The action applied while a human occupant is explicitly absent.</summary>
public enum OccupantAbsenceAction
{
    Retain = 1,
    Escalate = 2,
}

/// <summary>Stable wire values for the basic-absence action vocabulary.</summary>
public static class OccupantAbsenceActionContract
{
    public static OccupantAbsenceAction RequireDefined(
        OccupantAbsenceAction value,
        string parameterName) => value switch
        {
            OccupantAbsenceAction.Retain or OccupantAbsenceAction.Escalate => value,
            _ => throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Occupant absence action must be Retain or Escalate."),
        };

    public static string ToWireValue(OccupantAbsenceAction value) =>
        RequireDefined(value, nameof(value)) switch
        {
            OccupantAbsenceAction.Retain => "retain",
            OccupantAbsenceAction.Escalate => "escalate",
            _ => throw new InvalidOperationException("Validated absence action is not mapped."),
        };

    public static bool TryParseWireValue(string? value, out OccupantAbsenceAction result)
    {
        switch (value)
        {
            case "retain":
                result = OccupantAbsenceAction.Retain;
                return true;
            case "escalate":
                result = OccupantAbsenceAction.Escalate;
                return true;
            default:
                result = default;
                return false;
        }
    }
}

/// <summary>
/// Basic declarative absence. Presence means the human occupant is currently absent; removing the
/// block makes the occupant available again. Time-bounded periods and substitutes belong to F3.
/// </summary>
public sealed record OccupantAbsenceConfiguration
{
    public OccupantAbsenceConfiguration(OccupantAbsenceAction action)
    {
        Action = OccupantAbsenceActionContract.RequireDefined(action, nameof(action));
    }

    public OccupantAbsenceAction Action { get; }
}
