namespace Hive.Infrastructure.Scheduling;

public sealed record SchedulerPulseDeliveryReason
{
    public SchedulerPulseDeliveryReason(string code, string message)
    {
        Code = RequireText(code, nameof(code));
        Message = RequireText(message, nameof(message));
    }

    public string Code { get; }

    public string Message { get; }

    private static string RequireText(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty or whitespace.", parameterName);
        }

        return value.Trim();
    }
}
