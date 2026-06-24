namespace Hive.Domain.Identity;

internal static class IdentityValue
{
    public static string RequireStructural(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Identity value cannot be empty or whitespace.", parameterName);
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Identity value cannot contain leading or trailing whitespace.", parameterName);
        }

        return value;
    }

    public static string RequireWithout(string value, char forbidden, string parameterName)
    {
        if (value.IndexOf(forbidden) >= 0)
        {
            throw new ArgumentException(
                $"Identity value cannot contain the '{forbidden}' character.",
                parameterName);
        }

        return value;
    }

    public static Guid RequireMessage(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Identity value cannot be an empty Guid.", parameterName);
        }

        return value;
    }
}
