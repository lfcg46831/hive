using System.Collections.Immutable;

namespace Hive.Domain.Organization.Configuration.Validation;

/// <summary>
/// The aggregated outcome of validating an already-typed organization configuration
/// (US-F0-05-T05–T07): every semantic <see cref="Errors">error</see> the validator could surface in a
/// single pass, deterministically ordered. Mirrors the aggregating, exceptions-are-not-flow-control
/// style of the message <c>ValidationResult</c> of §9.8 so configuration diagnostics, test payloads
/// and audit records stay stable across runs.
/// </summary>
public sealed record OrganizationConfigurationValidationResult
{
    private OrganizationConfigurationValidationResult(
        ImmutableArray<OrganizationConfigurationValidationError> errors)
    {
        Errors = errors;
    }

    /// <summary>The shared, allocation-free result of a configuration with no semantic problems.</summary>
    public static OrganizationConfigurationValidationResult Valid { get; } = new([]);

    /// <summary>The semantic errors found; empty when <see cref="IsValid"/>.</summary>
    public IReadOnlyList<OrganizationConfigurationValidationError> Errors { get; }

    /// <summary>Whether the configuration carried no semantic problems.</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>
    /// Aggregates <paramref name="errors"/> into a result, snapshotting the input, removing exact
    /// duplicates and ordering deterministically by <see cref="OrganizationConfigurationValidationError.Path"/>,
    /// then <see cref="OrganizationConfigurationValidationError.Code"/>, then
    /// <see cref="OrganizationConfigurationValidationError.Message"/> (all ordinal). Returns the shared
    /// <see cref="Valid"/> instance when the sequence is empty.
    /// </summary>
    public static OrganizationConfigurationValidationResult Create(
        IEnumerable<OrganizationConfigurationValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var snapshot = errors.ToImmutableArray();

        if (snapshot.Any(error => error is null))
        {
            throw new ArgumentException(
                "Validation errors cannot contain null entries.",
                nameof(errors));
        }

        if (snapshot.IsEmpty)
        {
            return Valid;
        }

        return new OrganizationConfigurationValidationResult(
            snapshot
                .Distinct()
                .OrderBy(error => error.Path, StringComparer.Ordinal)
                .ThenBy(error => error.Code, StringComparer.Ordinal)
                .ThenBy(error => error.Message, StringComparer.Ordinal)
                .ToImmutableArray());
    }
}
