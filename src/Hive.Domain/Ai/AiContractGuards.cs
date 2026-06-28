using System.Collections.Immutable;

namespace Hive.Domain.Ai;

internal static class AiContractGuards
{
    public static string RequireText(string value, string parameterName)
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

    public static string? OptionalText(string? value, string parameterName) =>
        value is null ? null : RequireText(value, parameterName);

    public static ImmutableArray<T> Snapshot<T>(IEnumerable<T>? values, string parameterName)
        where T : class
    {
        if (values is null)
        {
            return [];
        }

        var snapshot = values.ToImmutableArray();

        if (snapshot.Any(value => value is null))
        {
            throw new ArgumentException("Collection cannot contain null entries.", parameterName);
        }

        return snapshot;
    }

    public static ImmutableDictionary<string, string> SnapshotMetadata(
        IReadOnlyDictionary<string, string>? metadata,
        string parameterName)
    {
        if (metadata is null)
        {
            return ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);
        }

        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        foreach (var (key, value) in metadata)
        {
            builder[RequireText(key, parameterName)] = RequireText(value, parameterName);
        }

        return builder.ToImmutable();
    }

    public static ImmutableDictionary<string, object?> SnapshotData(
        IReadOnlyDictionary<string, object?>? data,
        string parameterName)
    {
        if (data is null)
        {
            return ImmutableDictionary<string, object?>.Empty.WithComparers(StringComparer.Ordinal);
        }

        var builder = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);

        foreach (var (key, value) in data)
        {
            builder[RequireText(key, parameterName)] = value;
        }

        return builder.ToImmutable();
    }
}
