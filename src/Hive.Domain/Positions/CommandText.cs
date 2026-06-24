namespace Hive.Domain.Positions;

/// <summary>
/// Validation helpers for the free-text fields carried by <see cref="PositionCommand"/> records.
/// Mirrors the structural identity rules: a required field cannot be null, empty, whitespace, or
/// carry leading/trailing whitespace, so persisted commands never round-trip ambiguous content.
/// </summary>
internal static class CommandText
{
    public static string RequireContent(string value, string parameterName)
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
}
