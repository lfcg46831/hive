namespace Hive.Domain.Scheduling;

/// <summary>
/// Structural validation helpers for the free-text fields of the scheduler domain model. Mirrors the
/// identity rules so persisted schedule definitions never round-trip ambiguous content.
/// </summary>
internal static class SchedulingText
{
    /// <summary>
    /// Requires a single-line token: non-null, non-empty and without leading/trailing whitespace
    /// (used for cron expressions and timezone identifiers).
    /// </summary>
    public static string RequireToken(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty or whitespace.", parameterName);
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Value cannot contain leading or trailing whitespace.", parameterName);
        }

        return value;
    }

    /// <summary>
    /// Requires a content payload: non-null and not whitespace-only. Inner whitespace and newlines
    /// are preserved because a Pulse payload carries free-form instruction text.
    /// </summary>
    public static string RequireContent(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty or whitespace.", parameterName);
        }

        return value;
    }
}
