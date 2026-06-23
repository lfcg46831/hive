using System.Globalization;

namespace Hive.Domain.Organization.Configuration.Validation;

/// <summary>
/// Enforces the structural rules of §4/§4.8 over an already-typed <see cref="OrganizationConfiguration"/>
/// (US-F0-05-T07): the shape the command and unit trees must have before the registry/read model is
/// materialized. Three rules, over the same model uniqueness (US-F0-05-T05) and cross-references
/// (US-F0-05-T06) are validated against:
/// <list type="bullet">
/// <item><description>
/// <b>Exactly one leadership per unit (§4.1).</b> Each unit declares a single
/// <c>units[].leadership</c> by construction, so the structural concern is coherence: a unit's
/// leadership is a position that <i>belongs to that unit</i>, and no single position is the leadership
/// of more than one unit.
/// </description></item>
/// <item><description>
/// <b>No cycles in the organizational tree.</b> The <c>units[].parent</c> edges form the unit tree
/// (§2 glossary, the unit "forma a árvore organizacional"): exactly one unit — the declared
/// <c>organization.root_unit</c> — has a null parent, every other unit declares a parent that resolves
/// to a declared unit, and the parent edges are acyclic.
/// </description></item>
/// <item><description>
/// <b>All positions belong to a unit.</b> Each <c>positions[].unit</c> is a non-null <c>UnitId</c> by
/// construction of the typed model and is checked to resolve to a declared unit by the cross-reference
/// validator, so membership itself can never be absent here; this validator's contribution to the rule
/// is the leadership-belongs-to-its-unit coherence above.
/// </description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// The acyclicity of the command tree (<c>positions[].reports_to</c>) — exactly one position without a
/// superior and no cycles in the superior edges — is an internal invariant of the materialized
/// <c>OrganizationRelationsSnapshot</c> (US-F0-04-T02); the organizational tree of this rule is the
/// <c>units[].parent</c> tree the contract assigns to this task. Resolution of <c>units[].parent</c>
/// is owned here rather than by the cross-reference validator because the parent edges cannot be
/// walked for acyclicity without first resolving them.
/// </para>
/// <para>
/// Following §9.8, every violation is reported rather than thrown, in a single pass, so a document with
/// several structural problems yields all of them at once. Each error is located at the dotted path of
/// the offending field and identifiers compare with the ordinal, case-sensitive semantics of §9.1. The
/// validator is independent of uniqueness and cross-references: it tolerates duplicate or unresolved
/// ids without masking or being masked by them, and it never reports a problem another validator owns
/// (for example a leadership position that does not exist at all is left to US-F0-05-T06).
/// </para>
/// </remarks>
public static class OrganizationConfigurationStructuralValidator
{
    /// <summary>
    /// Validates the structural rules over <paramref name="configuration"/>, returning every violation
    /// found aggregated and deterministically ordered, or
    /// <see cref="OrganizationConfigurationValidationResult.Valid"/> when the structure is sound.
    /// </summary>
    public static OrganizationConfigurationValidationResult Validate(
        OrganizationConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var errors = new List<OrganizationConfigurationValidationError>();

        ValidateLeadership(configuration, errors);
        ValidateUnitTree(configuration, errors);

        return OrganizationConfigurationValidationResult.Create(errors);
    }

    /// <summary>
    /// Rule 1 — exactly one leadership per unit (§4.1): each unit's leadership is a position that
    /// belongs to that same unit, and no position is the leadership of more than one unit.
    /// </summary>
    private static void ValidateLeadership(
        OrganizationConfiguration configuration,
        List<OrganizationConfigurationValidationError> errors)
    {
        // The unit each position belongs to (positions[].unit), first declaration wins so a duplicate
        // position id — a uniqueness problem (US-F0-05-T05) — does not mask this rule.
        var unitByPosition = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var p = 0; p < configuration.Positions.Count; p++)
        {
            var position = configuration.Positions[p];
            unitByPosition.TryAdd(position.Id.Value, position.Unit.Value);
        }

        // The unit that first claimed each leadership position, to detect a position leading two units.
        var unitByLeadership = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var u = 0; u < configuration.Units.Count; u++)
        {
            var unit = configuration.Units[u];
            var unitId = unit.Id.Value;
            var leadership = unit.Leadership.Value;

            // A unit's leadership must belong to the unit it leads. A leadership that resolves to no
            // declared position at all is a cross-reference problem (US-F0-05-T06), not handled here.
            if (unitByPosition.TryGetValue(leadership, out var owningUnit)
                && !string.Equals(owningUnit, unitId, StringComparison.Ordinal))
            {
                errors.Add(new OrganizationConfigurationValidationError(
                    "leadership-not-in-unit",
                    $"units[{u}].leadership",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The leadership position '{leadership}' of unit '{unitId}' belongs to unit '{owningUnit}', not to the unit it leads.")));
            }

            // A position can lead at most one unit: report each unit past the first that reuses it.
            if (unitByLeadership.TryGetValue(leadership, out var firstUnit))
            {
                errors.Add(new OrganizationConfigurationValidationError(
                    "position-leads-multiple-units",
                    $"units[{u}].leadership",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The position '{leadership}' is already the leadership of unit '{firstUnit}' and cannot also lead unit '{unitId}'.")));
            }
            else
            {
                unitByLeadership.Add(leadership, unitId);
            }
        }
    }

    /// <summary>
    /// Rule 2 — no cycles in the organizational tree: the <c>units[].parent</c> edges form a single
    /// tree rooted at <c>organization.root_unit</c> (the only unit with a null parent) with every other
    /// parent resolving to a declared unit and no cycles.
    /// </summary>
    private static void ValidateUnitTree(
        OrganizationConfiguration configuration,
        List<OrganizationConfigurationValidationError> errors)
    {
        var declaredUnits = new HashSet<string>(StringComparer.Ordinal);
        for (var u = 0; u < configuration.Units.Count; u++)
        {
            declaredUnits.Add(configuration.Units[u].Id.Value);
        }

        var rootUnit = configuration.Organization.RootUnit.Value;

        // The resolved parent of each unit id — null when the unit has no parent or names one that does
        // not resolve; the latter is reported separately and ends the chain so it cannot feign a cycle.
        var resolvedParent = new Dictionary<string, string?>(StringComparer.Ordinal);

        for (var u = 0; u < configuration.Units.Count; u++)
        {
            var unit = configuration.Units[u];
            var unitId = unit.Id.Value;
            var isRoot = string.Equals(unitId, rootUnit, StringComparison.Ordinal);

            if (unit.Parent is null)
            {
                // Only the root unit may omit its parent; any other parentless unit breaks the single
                // rooted tree.
                if (!isRoot)
                {
                    errors.Add(new OrganizationConfigurationValidationError(
                        "non-root-unit-without-parent",
                        $"units[{u}].parent",
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"The non-root unit '{unitId}' must declare a parent; only the root unit '{rootUnit}' has a null parent.")));
                }

                resolvedParent[unitId] = null;
                continue;
            }

            var parent = unit.Parent.Value;

            // The root unit must be rooted: it cannot declare a parent.
            if (isRoot)
            {
                errors.Add(new OrganizationConfigurationValidationError(
                    "root-unit-parent-not-null",
                    $"units[{u}].parent",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The root unit '{unitId}' must have a null parent but declares parent '{parent}'.")));
            }

            // A declared parent must resolve to a known unit; an unresolved parent ends the chain.
            if (declaredUnits.Contains(parent))
            {
                resolvedParent[unitId] = parent;
            }
            else
            {
                errors.Add(new OrganizationConfigurationValidationError(
                    "parent-unit-not-found",
                    $"units[{u}].parent",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The parent unit '{parent}' of unit '{unitId}' does not resolve to a declared unit.")));
                resolvedParent[unitId] = null;
            }
        }

        ReportParentCycles(configuration, resolvedParent, errors);
    }

    /// <summary>
    /// Reports a <c>unit-parent-cycle</c> for each unit that lies on a cycle of resolved parent edges.
    /// Each unit has at most one parent edge, so a unit is on a cycle exactly when following its
    /// parents leads back to itself; units that merely descend from a cycle are not reported, the cycle
    /// members carry the diagnostic.
    /// </summary>
    private static void ReportParentCycles(
        OrganizationConfiguration configuration,
        IReadOnlyDictionary<string, string?> resolvedParent,
        List<OrganizationConfigurationValidationError> errors)
    {
        var unitCount = configuration.Units.Count;

        for (var u = 0; u < unitCount; u++)
        {
            var startId = configuration.Units[u].Id.Value;

            // Walk parent edges; the chain returns to the start only when the start sits on a cycle.
            // The step bound (one per unit) caps walks that fall into a cycle below the start without
            // returning to it — an acyclic chain is strictly shorter than the number of units.
            var current = Parent(resolvedParent, startId);
            var onCycle = false;

            for (var step = 0; step < unitCount && current is not null; step++)
            {
                if (string.Equals(current, startId, StringComparison.Ordinal))
                {
                    onCycle = true;
                    break;
                }

                current = Parent(resolvedParent, current);
            }

            if (onCycle)
            {
                errors.Add(new OrganizationConfigurationValidationError(
                    "unit-parent-cycle",
                    $"units[{u}].parent",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The parent edges of unit '{startId}' form a cycle in the unit tree.")));
            }
        }
    }

    /// <summary>
    /// Returns the resolved parent of <paramref name="unitId"/>, or <see langword="null"/> when the unit
    /// is rooted, has an unresolved parent, or is not present in the resolved map.
    /// </summary>
    private static string? Parent(IReadOnlyDictionary<string, string?> resolvedParent, string unitId) =>
        resolvedParent.TryGetValue(unitId, out var parent) ? parent : null;
}
