using System.Collections.Immutable;

namespace Hive.Domain.Governance;

public enum ActionDomainGate
{
    Decide = 1,
    Escalate = 2,
    HumanApproval = 3,
}

public enum ActionDomainActionKind
{
    Tool = 1,
    OrganizationalMessage = 2,
}

/// <summary>Typed representation of the versioned <c>action-domains</c> catalog.</summary>
public sealed record ActionDomainCatalog
{
    public ActionDomainCatalog(
        int version,
        ActionDomainCatalogDefaults defaults,
        IReadOnlyList<ActionDomain> domains)
    {
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(version),
                version,
                "Catalog version must be positive.");
        }

        ArgumentNullException.ThrowIfNull(defaults);

        Version = version;
        Defaults = defaults;
        Domains = ActionDomainCatalogGuards.SnapshotRequired(domains, nameof(domains));
    }

    public int Version { get; }

    public ActionDomainCatalogDefaults Defaults { get; }

    public IReadOnlyList<ActionDomain> Domains { get; }
}

/// <summary>Explicit catalog defaults. F0 requires unmatched actions to be declared fail-closed.</summary>
public sealed record ActionDomainCatalogDefaults
{
    public ActionDomainCatalogDefaults(ActionDomainGate unmatchedAction)
    {
        UnmatchedAction = ActionDomainCatalogGuards.RequireDefined(unmatchedAction, nameof(unmatchedAction));
    }

    public ActionDomainGate UnmatchedAction { get; }
}

/// <summary>One declared action domain and its minimum governance gate.</summary>
public sealed record ActionDomain
{
    public ActionDomain(
        AuthorityKey key,
        string description,
        ActionDomainGate gate,
        IReadOnlyList<ActionDomainMatchPredicate> match)
    {
        ArgumentNullException.ThrowIfNull(key);

        Key = key;
        Description = ActionDomainCatalogGuards.RequireText(description, nameof(description));
        Gate = ActionDomainCatalogGuards.RequireDefined(gate, nameof(gate));
        Match = ActionDomainCatalogGuards.SnapshotRequired(match, nameof(match));
    }

    public AuthorityKey Key { get; }

    public string Description { get; }

    public ActionDomainGate Gate { get; }

    public IReadOnlyList<ActionDomainMatchPredicate> Match { get; }
}

/// <summary>Objective predicate over structured action attributes supplied by tool/message contracts.</summary>
public sealed record ActionDomainMatchPredicate
{
    public ActionDomainMatchPredicate(
        ActionDomainActionKind action,
        IReadOnlyDictionary<string, object> attributes)
    {
        Action = ActionDomainCatalogGuards.RequireDefined(action, nameof(action));
        Attributes = ActionDomainCatalogGuards.SnapshotAttributes(attributes, nameof(attributes));
    }

    public ActionDomainActionKind Action { get; }

    public IReadOnlyDictionary<string, object> Attributes { get; }
}

internal static class ActionDomainCatalogGuards
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

    public static TEnum RequireDefined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentException($"Value '{value}' is not a defined {typeof(TEnum).Name}.", parameterName);
        }

        return value;
    }

    public static ImmutableArray<T> SnapshotRequired<T>(IReadOnlyList<T> values, string parameterName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);

        var snapshot = values.ToImmutableArray();
        if (snapshot.Any(value => value is null))
        {
            throw new ArgumentException("Collection cannot contain null entries.", parameterName);
        }

        return snapshot;
    }

    public static ImmutableDictionary<string, object> SnapshotAttributes(
        IReadOnlyDictionary<string, object> attributes,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(attributes, parameterName);

        var builder = ImmutableDictionary.CreateBuilder<string, object>(StringComparer.Ordinal);

        foreach (var (key, value) in attributes)
        {
            if (value is null)
            {
                throw new ArgumentException("Predicate attribute values cannot be null.", parameterName);
            }

            var normalizedKey = RequireText(key, parameterName);
            if (normalizedKey.Any(char.IsWhiteSpace))
            {
                throw new ArgumentException("Predicate attribute keys cannot contain whitespace.", parameterName);
            }

            builder[normalizedKey] = value is string text
                ? RequireText(text, parameterName)
                : value;
        }

        return builder.ToImmutable();
    }
}
