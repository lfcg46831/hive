using System.Globalization;

namespace Hive.Domain.Organization.Configuration.Validation;

/// <summary>
/// Enforces the uniqueness rules of §4.8 over an already-typed <see cref="OrganizationConfiguration"/>
/// (US-F0-05-T05): unit ids, position ids and prompt-catalog ids must each be unique across the
/// document, and within a single occupant the declared schedule ids and the subscribed events must
/// not repeat. Cross-references (US-F0-05-T06) and structural rules (US-F0-05-T07) are validated
/// separately over the same model.
/// </summary>
/// <remarks>
/// <para>
/// Two of the id-spaces named by the contract cannot be duplicated in the typed model and so need no
/// runtime check: the document carries a single <c>organization</c> block, so its
/// <see cref="OrganizationHeader.Id">OrganizationId</see> is unique by construction; and each
/// position carries exactly one inline <see cref="PositionConfiguration.Occupant">occupant</see>, so
/// — once position ids are known to be unique — every occupant is unique too. The validator therefore
/// concentrates on the id-spaces a document can actually duplicate.
/// </para>
/// <para>
/// Following §9.8, every duplicate found is reported rather than thrown, in a single pass, so a
/// document with several collisions yields all of them at once. Each repeated entry past the first is
/// reported at its own dotted path, naming where the value was first declared, and identifiers compare
/// with the ordinal, case-sensitive semantics of §9.1.
/// </para>
/// </remarks>
public static class OrganizationConfigurationUniquenessValidator
{
    /// <summary>
    /// Validates the uniqueness rules over <paramref name="configuration"/>, returning every duplicate
    /// found aggregated and deterministically ordered, or
    /// <see cref="OrganizationConfigurationValidationResult.Valid"/> when none collide.
    /// </summary>
    public static OrganizationConfigurationValidationResult Validate(
        OrganizationConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var errors = new List<OrganizationConfigurationValidationError>();

        CollectDuplicates(
            configuration.Units,
            unit => unit.Id.Value,
            code: "duplicate-unit-id",
            label: "unit id",
            path: index => $"units[{index}].id",
            firstLocation: index => $"units[{index}]",
            errors);

        CollectDuplicates(
            configuration.Positions,
            position => position.Id.Value,
            code: "duplicate-position-id",
            label: "position id",
            path: index => $"positions[{index}].id",
            firstLocation: index => $"positions[{index}]",
            errors);

        CollectDuplicates(
            configuration.Prompts,
            prompt => prompt.Id,
            code: "duplicate-prompt-id",
            label: "prompt id",
            path: index => $"prompts[{index}].id",
            firstLocation: index => $"prompts[{index}]",
            errors);

        // Schedule ids and subscription events are scoped to a single occupant: the same id or event
        // may legitimately recur under different positions, so each occupant is checked in isolation.
        for (var p = 0; p < configuration.Positions.Count; p++)
        {
            var occupant = configuration.Positions[p].Occupant;
            var positionIndex = p;

            CollectDuplicates(
                occupant.Schedule,
                entry => entry.Id,
                code: "duplicate-schedule-id",
                label: "schedule id",
                path: index => $"positions[{positionIndex}].occupant.schedule[{index}].id",
                firstLocation: index => $"positions[{positionIndex}].occupant.schedule[{index}]",
                errors);

            CollectDuplicates(
                occupant.Subscriptions,
                subscription => subscription.Event,
                code: "duplicate-subscription-event",
                label: "subscription event",
                path: index => $"positions[{positionIndex}].occupant.subscriptions[{index}].event",
                firstLocation: index => $"positions[{positionIndex}].occupant.subscriptions[{index}]",
                errors);
        }

        return OrganizationConfigurationValidationResult.Create(errors);
    }

    /// <summary>
    /// Scans <paramref name="items"/> in declaration order and, for every entry whose
    /// <paramref name="keySelector"/> value was already seen, appends one error locating the repeat and
    /// pointing back at the first declaration. Keys compare ordinally (§9.1).
    /// </summary>
    private static void CollectDuplicates<T>(
        IReadOnlyList<T> items,
        Func<T, string> keySelector,
        string code,
        string label,
        Func<int, string> path,
        Func<int, string> firstLocation,
        List<OrganizationConfigurationValidationError> errors)
    {
        var firstSeenAt = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var index = 0; index < items.Count; index++)
        {
            var key = keySelector(items[index]);

            if (firstSeenAt.TryGetValue(key, out var originalIndex))
            {
                errors.Add(new OrganizationConfigurationValidationError(
                    code,
                    path(index),
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Duplicate {label} '{key}'; first declared at {firstLocation(originalIndex)}.")));
            }
            else
            {
                firstSeenAt[key] = index;
            }
        }
    }
}
