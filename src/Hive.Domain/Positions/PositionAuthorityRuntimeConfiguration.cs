using System.Collections.Immutable;
using Hive.Domain.Governance;

namespace Hive.Domain.Positions;

/// <summary>
/// Runtime sparse authority rules projected for a position occupant.
/// </summary>
public sealed record PositionAuthorityRuntimeConfiguration
{
    public PositionAuthorityRuntimeConfiguration(
        IEnumerable<string>? canDecide = null,
        IEnumerable<PositionAuthorityOverrideRuntimeConfiguration>? overrides = null)
    {
        CanDecide = ToAuthorityKeys(canDecide);
        Overrides = ToOverrides(overrides, nameof(overrides));
    }

    public ImmutableArray<AuthorityKey> CanDecide { get; }

    public ImmutableArray<PositionAuthorityOverrideRuntimeConfiguration> Overrides { get; }

    private static ImmutableArray<AuthorityKey> ToAuthorityKeys(IEnumerable<string>? source)
    {
        if (source is null)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<AuthorityKey>();
        foreach (var item in source)
        {
            builder.Add(AuthorityKey.From(item));
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<PositionAuthorityOverrideRuntimeConfiguration> ToOverrides(
        IEnumerable<PositionAuthorityOverrideRuntimeConfiguration>? source,
        string parameterName)
    {
        if (source is null)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<PositionAuthorityOverrideRuntimeConfiguration>();
        foreach (var item in source)
        {
            if (item is null)
            {
                throw new ArgumentException("Collection cannot contain null entries.", parameterName);
            }

            builder.Add(item);
        }

        return builder.ToImmutable();
    }
}

public sealed record PositionAuthorityOverrideRuntimeConfiguration
{
    public PositionAuthorityOverrideRuntimeConfiguration(
        string key,
        ActionDomainGate gate,
        string? approver = null)
    {
        Key = AuthorityKey.From(key);
        Gate = RequireDefined(gate, nameof(gate));
        Approver = approver is null
            ? null
            : CommandText.RequireContent(approver, nameof(approver));
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
}
