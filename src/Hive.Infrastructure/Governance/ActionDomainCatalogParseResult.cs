using System.Collections.Immutable;
using Hive.Domain.Governance;

namespace Hive.Infrastructure.Governance;

/// <summary>
/// The outcome of parsing an action-domain catalog YAML document.
/// </summary>
public sealed record ActionDomainCatalogParseResult
{
    private ActionDomainCatalogParseResult(
        ActionDomainCatalog? catalog,
        ImmutableArray<ActionDomainCatalogParseError> errors)
    {
        Catalog = catalog;
        Errors = errors;
    }

    public bool IsSuccess => Catalog is not null;

    public ActionDomainCatalog? Catalog { get; }

    public IReadOnlyList<ActionDomainCatalogParseError> Errors { get; }

    public static ActionDomainCatalogParseResult Success(ActionDomainCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        return new ActionDomainCatalogParseResult(
            catalog,
            ImmutableArray<ActionDomainCatalogParseError>.Empty);
    }

    public static ActionDomainCatalogParseResult Failure(IEnumerable<ActionDomainCatalogParseError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var snapshot = errors.ToImmutableArray();

        if (snapshot.Any(error => error is null))
        {
            throw new ArgumentException("Parse errors cannot contain null entries.", nameof(errors));
        }

        if (snapshot.IsEmpty)
        {
            throw new ArgumentException("A failed parse result must carry at least one error.", nameof(errors));
        }

        var ordered = snapshot
            .OrderBy(error => error.Line ?? int.MaxValue)
            .ThenBy(error => error.Column ?? int.MaxValue)
            .ThenBy(error => error.FieldPath, StringComparer.Ordinal)
            .ThenBy(error => error.Message, StringComparer.Ordinal)
            .ToImmutableArray();

        return new ActionDomainCatalogParseResult(null, ordered);
    }
}
