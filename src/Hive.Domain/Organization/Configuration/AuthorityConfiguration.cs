using System.Collections.Immutable;
using Hive.Domain.Governance;

namespace Hive.Domain.Organization.Configuration;

/// <summary>
/// Sparse position authority from §4.9: action domains this position can decide plus optional
/// gate-tightening overrides.
/// </summary>
public sealed record AuthorityConfiguration
{
    public AuthorityConfiguration(
        IReadOnlyList<string>? canDecide = null,
        IReadOnlyList<AuthorityOverrideConfiguration>? overrides = null)
    {
        CanDecide = SnapshotAuthorityKeys(canDecide);
        Overrides = SnapshotOverrides(overrides, nameof(overrides));
    }

    public IReadOnlyList<AuthorityKey> CanDecide { get; }

    public IReadOnlyList<AuthorityOverrideConfiguration> Overrides { get; }

    private static ImmutableArray<AuthorityKey> SnapshotAuthorityKeys(IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<AuthorityKey>(values.Count);
        foreach (var value in values)
        {
            builder.Add(AuthorityKey.From(value));
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<AuthorityOverrideConfiguration> SnapshotOverrides(
        IReadOnlyList<AuthorityOverrideConfiguration>? values,
        string parameterName)
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
}

public sealed record AuthorityOverrideConfiguration
{
    public AuthorityOverrideConfiguration(
        string key,
        ActionDomainGate gate,
        string? approver = null)
    {
        Key = AuthorityKey.From(key);
        Gate = RequireDefined(gate, nameof(gate));
        Approver = approver is null ? null : RequireText(approver, nameof(approver));
    }

    public AuthorityKey Key { get; }

    public ActionDomainGate Gate { get; }

    public string? Approver { get; }

    private static TEnum RequireDefined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentException($"Value '{value}' is not a defined {typeof(TEnum).Name}.", parameterName);
        }

        return value;
    }

    private static string RequireText(string value, string parameterName)
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
