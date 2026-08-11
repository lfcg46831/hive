namespace Hive.Domain.OccupantChannels;

internal static class OccupantChannelContractGuards
{
    public static string RequireRenderedMessage(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "Rendered occupant-channel message cannot be empty or whitespace.",
                parameterName);
        }

        return value;
    }

    public static string RequireOpaqueToken(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "Occupant-channel correlation token cannot be empty or whitespace.",
                parameterName);
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Occupant-channel correlation token cannot contain leading or trailing whitespace.",
                parameterName);
        }

        return value;
    }
}
