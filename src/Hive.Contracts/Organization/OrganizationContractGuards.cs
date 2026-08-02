using System.Collections.Immutable;

namespace Hive.Contracts.Organization;

internal static class OrganizationContractGuards
{
    private const int MaxIdentifierLength = 256;
    private const int MaxDisplayNameLength = 1_024;
    private const string FingerprintPrefix = "sha256:";
    private const int Sha256HexLength = 64;

    public static string Identifier(string value, string parameterName) =>
        Text(value, parameterName, MaxIdentifierLength);

    public static string? OptionalIdentifier(string? value, string parameterName) =>
        value is null ? null : Identifier(value, parameterName);

    public static string? OptionalDisplayName(string? value, string parameterName) =>
        value is null ? null : Text(value, parameterName, MaxDisplayNameLength);

    public static string Fingerprint(string value, string parameterName)
    {
        var fingerprint = Text(
            value,
            parameterName,
            FingerprintPrefix.Length + Sha256HexLength);
        if (fingerprint.Length != FingerprintPrefix.Length + Sha256HexLength ||
            !fingerprint.StartsWith(FingerprintPrefix, StringComparison.Ordinal) ||
            fingerprint.AsSpan(FingerprintPrefix.Length).ContainsAnyExcept("0123456789abcdef"))
        {
            throw new ArgumentException(
                "Registry fingerprint must be a lowercase SHA-256 value prefixed with 'sha256:'.",
                parameterName);
        }

        return fingerprint;
    }

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

    public static IReadOnlyList<string> SortedIdentifiers(
        IReadOnlyList<string> values,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var snapshot = values
            .Select(value => Identifier(value, parameterName))
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        RejectDuplicateKeys(snapshot, value => value, parameterName);
        return snapshot;
    }

    public static IReadOnlyList<T> SortedSnapshot<T>(
        IReadOnlyList<T> values,
        Func<T, string> keySelector,
        string parameterName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        ArgumentNullException.ThrowIfNull(keySelector);

        var snapshot = values.ToImmutableArray();
        if (snapshot.Any(value => value is null))
        {
            throw new ArgumentException("Collection cannot contain null values.", parameterName);
        }

        var sorted = snapshot
            .OrderBy(keySelector, StringComparer.Ordinal)
            .ToImmutableArray();
        RejectDuplicateKeys(sorted, keySelector, parameterName);
        return sorted;
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

    private static void RejectDuplicateKeys<T>(
        IReadOnlyList<T> values,
        Func<T, string> keySelector,
        string parameterName)
    {
        for (var index = 1; index < values.Count; index++)
        {
            if (string.Equals(
                    keySelector(values[index - 1]),
                    keySelector(values[index]),
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Collection cannot contain duplicate stable keys.",
                    parameterName);
            }
        }
    }
}
