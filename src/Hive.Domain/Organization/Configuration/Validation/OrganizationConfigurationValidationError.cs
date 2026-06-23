namespace Hive.Domain.Organization.Configuration.Validation;

/// <summary>
/// One semantic problem found while validating an already-typed organization configuration
/// (US-F0-05-T05–T07): a stable machine-readable <see cref="Code"/> (for example
/// <c>duplicate-unit-id</c>), the dotted <see cref="Path"/> of the offending field
/// (for example <c>units[2].id</c> or <c>positions[0].occupant.schedule[1].id</c>) and a
/// human-readable <see cref="Message"/>.
/// </summary>
/// <remarks>
/// Semantic errors are raised over the typed <see cref="OrganizationConfiguration"/>, which no longer
/// carries YAML line/column positions, so — unlike the parse-level error of US-F0-05-T04 — this type
/// locates a problem by its dotted field <see cref="Path"/> alone. It is shared by the uniqueness
/// (US-F0-05-T05), cross-reference (US-F0-05-T06) and structural (US-F0-05-T07) validators.
/// </remarks>
public sealed record OrganizationConfigurationValidationError(
    string Code,
    string Path,
    string Message);
