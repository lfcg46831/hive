using System.Collections.Immutable;
using Hive.Domain.Organization.Configuration;

namespace Hive.Infrastructure.Organization.Configuration;

/// <summary>
/// The outcome of parsing an organization YAML document (US-F0-05-T04): either the typed
/// <see cref="Configuration"/> when the document was well-formed, or the aggregated list of readable
/// <see cref="Errors"/> describing every parse-level problem found. Mirrors the aggregating,
/// exceptions-are-not-flow-control style of the message <c>ValidationResult</c> of §9.8: a malformed
/// document yields all the errors the parser could surface in one pass, deterministically ordered.
/// </summary>
public sealed record OrganizationConfigurationParseResult
{
    private OrganizationConfigurationParseResult(
        OrganizationConfiguration? configuration,
        ImmutableArray<OrganizationConfigurationParseError> errors)
    {
        Configuration = configuration;
        Errors = errors;
    }

    /// <summary>Whether the document parsed into a complete typed configuration.</summary>
    public bool IsSuccess => Configuration is not null;

    /// <summary>The parsed configuration when <see cref="IsSuccess"/>; otherwise <see langword="null"/>.</summary>
    public OrganizationConfiguration? Configuration { get; }

    /// <summary>The parse-level errors; empty when <see cref="IsSuccess"/>.</summary>
    public IReadOnlyList<OrganizationConfigurationParseError> Errors { get; }

    /// <summary>Creates a successful result wrapping <paramref name="configuration"/>.</summary>
    public static OrganizationConfigurationParseResult Success(OrganizationConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new OrganizationConfigurationParseResult(
            configuration,
            ImmutableArray<OrganizationConfigurationParseError>.Empty);
    }

    /// <summary>Creates a failed result from one or more <paramref name="errors"/>.</summary>
    public static OrganizationConfigurationParseResult Failure(
        IEnumerable<OrganizationConfigurationParseError> errors)
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

        return new OrganizationConfigurationParseResult(null, ordered);
    }
}
