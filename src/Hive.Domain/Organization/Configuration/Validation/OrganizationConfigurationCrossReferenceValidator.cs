using System.Globalization;

namespace Hive.Domain.Organization.Configuration.Validation;

/// <summary>
/// Enforces the cross-reference rules of §4.8 over an already-typed <see cref="OrganizationConfiguration"/>
/// (US-F0-05-T06): every identifier a document uses to point at another declared entity must resolve.
/// The schema fixes exactly these resolvable edges, and this validator checks each one:
/// <list type="bullet">
/// <item><description>the root unit (<c>organization.root_unit</c>) exists in <c>units</c> — the root leadership;</description></item>
/// <item><description>each unit leadership (<c>units[].leadership</c>) exists in <c>positions</c>;</description></item>
/// <item><description>each position's unit (<c>positions[].unit</c>) — the unit it occupies — exists in <c>units</c>;</description></item>
/// <item><description>each direct superior (<c>positions[].reports_to</c>, when declared) exists in <c>positions</c>;</description></item>
/// <item><description>each referenced identity prompt (<c>occupant.identity_prompt_ref</c>, when declared) exists in <c>prompts</c>;</description></item>
/// <item><description>the <c>OrganizationOwner</c> (<c>organization.owner.ref</c>) is declared.</description></item>
/// </list>
/// Uniqueness (US-F0-05-T05) and structural rules — single leadership per unit, acyclicity, every
/// position belonging to a unit (US-F0-05-T07) — are validated separately over the same model.
/// </summary>
/// <remarks>
/// <para>
/// The §4.8 occupant sub-blocks named alongside these references — <c>schedule[]</c> and <c>tools[]</c>
/// — carry no document-internal target to resolve: a schedule entry names only its own id, cron and
/// instruction, and a tool's connector/scope is matched against the runtime HIVE allowlist (§6.4), not
/// a catalog declared in the document. They are therefore outside the cross-reference surface and are
/// validated for uniqueness (US-F0-05-T05) and at runtime, not here.
/// </para>
/// <para>
/// Following §9.8, every unresolved reference is reported rather than thrown, in a single pass, so a
/// document with several broken references yields all of them at once. Each error is located at the
/// dotted path of the offending reference and names the value that failed to resolve; identifiers
/// compare with the ordinal, case-sensitive semantics of §9.1. The validator is independent of
/// uniqueness: a reference resolves as long as the target id is declared at least once, so a document
/// can be reported for both a duplicate and a broken reference without either masking the other.
/// </para>
/// </remarks>
public static class OrganizationConfigurationCrossReferenceValidator
{
    /// <summary>
    /// Validates the cross-reference rules over <paramref name="configuration"/>, returning every
    /// unresolved reference found aggregated and deterministically ordered, or
    /// <see cref="OrganizationConfigurationValidationResult.Valid"/> when all references resolve.
    /// </summary>
    public static OrganizationConfigurationValidationResult Validate(
        OrganizationConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var errors = new List<OrganizationConfigurationValidationError>();

        var declaredUnits = ToIdSet(configuration.Units, unit => unit.Id.Value);
        var declaredPositions = ToIdSet(configuration.Positions, position => position.Id.Value);
        var declaredPrompts = ToIdSet(configuration.Prompts, prompt => prompt.Id);

        // organization.owner.ref — the OrganizationOwner must be declared (§4.2 top of the chain).
        if (string.IsNullOrWhiteSpace(configuration.Organization.Owner.Ref))
        {
            errors.Add(new OrganizationConfigurationValidationError(
                "organization-owner-required",
                "organization.owner.ref",
                "The OrganizationOwner reference is required but is missing or blank."));
        }

        // organization.root_unit — the root unit must exist (the root leadership is led from here).
        var rootUnit = configuration.Organization.RootUnit.Value;
        if (!declaredUnits.Contains(rootUnit))
        {
            errors.Add(Unresolved(
                "root-unit-not-found",
                "organization.root_unit",
                "root unit",
                rootUnit,
                "unit"));
        }

        // units[].leadership — every unit is led by a declared position.
        for (var u = 0; u < configuration.Units.Count; u++)
        {
            var leadership = configuration.Units[u].Leadership.Value;
            if (!declaredPositions.Contains(leadership))
            {
                errors.Add(Unresolved(
                    "leadership-position-not-found",
                    $"units[{u}].leadership",
                    "unit leadership",
                    leadership,
                    "position"));
            }
        }

        for (var p = 0; p < configuration.Positions.Count; p++)
        {
            var position = configuration.Positions[p];

            // positions[].unit — the unit the position occupies is declared.
            var unit = position.Unit.Value;
            if (!declaredUnits.Contains(unit))
            {
                errors.Add(Unresolved(
                    "unit-not-found",
                    $"positions[{p}].unit",
                    "position unit",
                    unit,
                    "unit"));
            }

            // positions[].reports_to — the direct superior, when declared, is a declared position.
            if (position.ReportsTo is { } reportsTo && !declaredPositions.Contains(reportsTo.Value))
            {
                errors.Add(Unresolved(
                    "reports-to-position-not-found",
                    $"positions[{p}].reports_to",
                    "direct superior",
                    reportsTo.Value,
                    "position"));
            }

            // occupant.identity_prompt_ref — the referenced prompt, when declared, exists in the catalog.
            var promptRef = position.Occupant.IdentityPromptRef;
            if (promptRef is not null && !declaredPrompts.Contains(promptRef))
            {
                errors.Add(Unresolved(
                    "identity-prompt-not-found",
                    $"positions[{p}].occupant.identity_prompt_ref",
                    "identity prompt reference",
                    promptRef,
                    "prompt"));
            }
        }

        return OrganizationConfigurationValidationResult.Create(errors);
    }

    /// <summary>
    /// Snapshots the declared identifiers of <paramref name="items"/> into an ordinal, case-sensitive
    /// set (§9.1). Duplicate ids collapse to a single entry, so references resolve independently of the
    /// uniqueness rules of US-F0-05-T05.
    /// </summary>
    private static HashSet<string> ToIdSet<T>(IReadOnlyList<T> items, Func<T, string> idSelector)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < items.Count; index++)
        {
            set.Add(idSelector(items[index]));
        }

        return set;
    }

    /// <summary>
    /// Builds one error for a reference <paramref name="value"/> at <paramref name="path"/> that did not
    /// resolve to a declared <paramref name="targetKind"/>.
    /// </summary>
    private static OrganizationConfigurationValidationError Unresolved(
        string code,
        string path,
        string label,
        string value,
        string targetKind) =>
        new(
            code,
            path,
            string.Create(
                CultureInfo.InvariantCulture,
                $"The {label} '{value}' does not resolve to a declared {targetKind}."));
}
