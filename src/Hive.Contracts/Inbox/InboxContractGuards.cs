namespace Hive.Contracts.Inbox;

internal static class InboxContractGuards
{
    private const int MaxIdentifierLength = 256;
    private const int MaxItemIdentifierLength = 512;
    private const int MaxDisplayTextLength = 4_096;

    public static string Identifier(string value, string parameterName) =>
        Text(value, parameterName, MaxIdentifierLength);

    public static string ItemIdentifier(string value, string parameterName) =>
        Text(value, parameterName, MaxItemIdentifierLength);

    public static string DisplayText(string value, string parameterName) =>
        Text(value, parameterName, MaxDisplayTextLength);

    public static Guid MessageIdentifier(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Message identifier cannot be empty.", parameterName);
        }

        return value;
    }

    public static Guid? OptionalMessageIdentifier(Guid? value, string parameterName) =>
        value is null ? null : MessageIdentifier(value.Value, parameterName);

    public static DateTimeOffset UtcTimestamp(
        DateTimeOffset value,
        string parameterName)
    {
        if (value == default)
        {
            throw new ArgumentException("Timestamp must be specified.", parameterName);
        }

        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use the UTC offset.", parameterName);
        }

        return value;
    }

    public static DateTimeOffset? OptionalUtcTimestamp(
        DateTimeOffset? value,
        string parameterName) =>
        value is null ? null : UtcTimestamp(value.Value, parameterName);

    public static T DefinedEnum<T>(T value, string parameterName)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Value is not defined.");
        }

        return value;
    }

    private static string Text(
        string value,
        string parameterName,
        int maxLength)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty or whitespace.", parameterName);
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Value cannot contain leading or trailing whitespace.",
                parameterName);
        }

        if (value.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value.Length,
                $"Value cannot exceed {maxLength} characters.");
        }

        return value;
    }
}
