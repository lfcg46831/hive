namespace Hive.Domain.Messaging;

public enum Priority
{
    Low = 1,
    Normal = 2,
    High = 3,
    Critical = 4,
}

public static class PriorityContract
{
    public static Priority RequireDefined(Priority value, string parameterName)
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Priority must be Low, Normal, High, or Critical.");
        }

        return value;
    }

    public static int Compare(Priority left, Priority right)
    {
        RequireDefined(left, nameof(left));
        RequireDefined(right, nameof(right));

        return ((int)left).CompareTo((int)right);
    }

    public static string ToWireValue(Priority value) =>
        RequireDefined(value, nameof(value)) switch
        {
            Priority.Low => "low",
            Priority.Normal => "normal",
            Priority.High => "high",
            Priority.Critical => "critical",
            _ => throw new InvalidOperationException("Validated priority is not mapped."),
        };

    public static Priority ParseWireValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value switch
        {
            "low" => Priority.Low,
            "normal" => Priority.Normal,
            "high" => Priority.High,
            "critical" => Priority.Critical,
            _ => throw new ArgumentException(
                "Priority must be low, normal, high, or critical.",
                nameof(value)),
        };
    }

    public static bool TryParseWireValue(string? value, out Priority priority)
    {
        switch (value)
        {
            case "low":
                priority = Priority.Low;
                return true;
            case "normal":
                priority = Priority.Normal;
                return true;
            case "high":
                priority = Priority.High;
                return true;
            case "critical":
                priority = Priority.Critical;
                return true;
            default:
                priority = default;
                return false;
        }
    }
}
