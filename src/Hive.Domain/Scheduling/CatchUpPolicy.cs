namespace Hive.Domain.Scheduling;

/// <summary>
/// The catch-up behaviour of a schedule for windows that elapsed while the scheduler was down
/// (US-F0-09-T10). By default a missed non-critical window is dropped; critical schedules may replay a
/// single, idempotent catch-up firing.
/// </summary>
public enum CatchUpPolicy
{
    /// <summary>Missed windows are ignored (with audit); the default for non-critical schedules.</summary>
    Skip = 1,

    /// <summary>At most one missed window is replayed idempotently; reserved for critical schedules.</summary>
    CatchUpOnce = 2,
}

/// <summary>Contract helpers for <see cref="CatchUpPolicy"/>, mirroring the messaging enum wire contracts.</summary>
public static class CatchUpPolicyContract
{
    public static CatchUpPolicy RequireDefined(CatchUpPolicy value, string parameterName)
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "CatchUpPolicy must be Skip or CatchUpOnce.");
        }

        return value;
    }

    public static string ToWireValue(CatchUpPolicy value) =>
        RequireDefined(value, nameof(value)) switch
        {
            CatchUpPolicy.Skip => "skip",
            CatchUpPolicy.CatchUpOnce => "catch-up-once",
            _ => throw new InvalidOperationException("Validated catch-up policy is not mapped."),
        };

    public static CatchUpPolicy ParseWireValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value switch
        {
            "skip" => CatchUpPolicy.Skip,
            "catch-up-once" => CatchUpPolicy.CatchUpOnce,
            _ => throw new ArgumentException(
                "CatchUpPolicy must be skip or catch-up-once.",
                nameof(value)),
        };
    }

    public static bool TryParseWireValue(string? value, out CatchUpPolicy policy)
    {
        switch (value)
        {
            case "skip":
                policy = CatchUpPolicy.Skip;
                return true;
            case "catch-up-once":
                policy = CatchUpPolicy.CatchUpOnce;
                return true;
            default:
                policy = default;
                return false;
        }
    }
}
