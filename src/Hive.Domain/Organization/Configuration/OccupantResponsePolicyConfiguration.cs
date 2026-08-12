using System.Xml;

namespace Hive.Domain.Organization.Configuration;

/// <summary>
/// Declarative human-response policy stored in the organization registry. Durations retain their
/// validated ISO-8601 wire representation for deterministic GitOps materialization.
/// </summary>
public sealed record OccupantResponsePolicyConfiguration
{
    public OccupantResponsePolicyConfiguration(
        int reminderMaxCount,
        string reminderInterval,
        string timeout)
    {
        if (reminderMaxCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reminderMaxCount),
                reminderMaxCount,
                "Reminder maximum count cannot be negative.");
        }

        var intervalText = RequireDuration(reminderInterval, nameof(reminderInterval));
        var timeoutText = RequireDuration(timeout, nameof(timeout));
        var intervalValue = ParsePositiveDuration(intervalText, nameof(reminderInterval));
        var timeoutValue = ParsePositiveDuration(timeoutText, nameof(timeout));
        var reminderHorizon = TimeSpan.FromTicks(
            checked(intervalValue.Ticks * reminderMaxCount));
        if (timeoutValue <= reminderHorizon)
        {
            throw new ArgumentException(
                "Response timeout must be greater than the last reminder horizon.",
                nameof(timeout));
        }

        ReminderMaxCount = reminderMaxCount;
        ReminderInterval = intervalText;
        Timeout = timeoutText;
    }

    public int ReminderMaxCount { get; }

    public string ReminderInterval { get; }

    public string Timeout { get; }

    private static string RequireDuration(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Duration cannot be empty or whitespace.", parameterName);
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Duration cannot contain leading or trailing whitespace.",
                parameterName);
        }

        return value;
    }

    private static TimeSpan ParsePositiveDuration(string value, string parameterName)
    {
        TimeSpan parsed;
        try
        {
            parsed = XmlConvert.ToTimeSpan(value);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new ArgumentException(
                "Duration must be a valid ISO-8601 duration.",
                parameterName,
                exception);
        }

        return parsed > TimeSpan.Zero
            ? parsed
            : throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Duration must be positive.");
    }
}
