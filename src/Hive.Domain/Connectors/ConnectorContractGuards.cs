using System.Collections.Immutable;
using Hive.Domain.Governance;

namespace Hive.Domain.Connectors;

internal static class ConnectorContractGuards
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
            throw new ArgumentException(
                "Value cannot contain leading or trailing whitespace.",
                parameterName);
        }

        return value;
    }

    public static string RequireToken(string value, string parameterName)
    {
        var token = RequireText(value, parameterName);
        if (token.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Token cannot contain whitespace.", parameterName);
        }

        return token;
    }

    public static string RequireConfigurationPath(string value, string parameterName)
    {
        var path = RequireText(value, parameterName);
        if (path[0] != '$')
        {
            throw new ArgumentException(
                "Connector configuration path must start at '$'.",
                parameterName);
        }

        return path;
    }

    public static string RequirePath(string value, string parameterName) =>
        RequireText(value, parameterName);

    public static string? OptionalContent(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "External message content cannot be empty or whitespace.",
                parameterName);
        }

        return value;
    }

    public static ConnectorScopeDirection RequireScopeDirection(
        ConnectorScopeDirection value,
        string parameterName)
    {
        if (value is not (ConnectorScopeDirection.Inbound
            or ConnectorScopeDirection.Outbound
            or ConnectorScopeDirection.Both))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Connector scope direction must be inbound, outbound or both.");
        }

        return value;
    }

    public static ImmutableArray<T> Snapshot<T>(
        IReadOnlyList<T>? values,
        string parameterName)
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

    public static ImmutableSortedDictionary<string, ActionAttributeValue> SnapshotAttributes(
        IReadOnlyDictionary<string, ActionAttributeValue>? values,
        string parameterName)
    {
        var builder = ImmutableSortedDictionary.CreateBuilder<string, ActionAttributeValue>(
            StringComparer.Ordinal);
        if (values is null)
        {
            return builder.ToImmutable();
        }

        foreach (var (key, value) in values)
        {
            var name = RequireToken(key, parameterName);
            if (value is null)
            {
                throw new ArgumentException(
                    "External message attributes cannot contain null values.",
                    parameterName);
            }

            builder.Add(name, value);
        }

        return builder.ToImmutable();
    }
}
