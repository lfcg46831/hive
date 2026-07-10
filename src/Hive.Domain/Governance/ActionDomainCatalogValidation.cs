using System.Collections.Immutable;
using System.Globalization;

namespace Hive.Domain.Governance;

public sealed record ActionDomainCatalogValidationError(
    string Code,
    string Path,
    string Message);

public sealed record ActionDomainCatalogValidationResult
{
    private ActionDomainCatalogValidationResult(
        ImmutableArray<ActionDomainCatalogValidationError> errors)
    {
        Errors = errors;
    }

    public static ActionDomainCatalogValidationResult Valid { get; } = new([]);

    public IReadOnlyList<ActionDomainCatalogValidationError> Errors { get; }

    public bool IsValid => Errors.Count == 0;

    public static ActionDomainCatalogValidationResult Create(
        IEnumerable<ActionDomainCatalogValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var snapshot = errors.ToImmutableArray();
        if (snapshot.Any(error => error is null))
        {
            throw new ArgumentException("Validation errors cannot contain null entries.", nameof(errors));
        }

        if (snapshot.IsEmpty)
        {
            return Valid;
        }

        return new ActionDomainCatalogValidationResult(
            snapshot
                .Distinct()
                .OrderBy(error => error.Path, StringComparer.Ordinal)
                .ThenBy(error => error.Code, StringComparer.Ordinal)
                .ThenBy(error => error.Message, StringComparer.Ordinal)
                .ToImmutableArray());
    }
}

public sealed record ActionDomainCatalogBinding
{
    public ActionDomainCatalogBinding(
        IReadOnlyList<ActionDomainAuthorityBinding>? authorities = null,
        IReadOnlyList<string>? declaredApprovers = null,
        IReadOnlyList<ActionDomainActionContract>? actionContracts = null)
    {
        Authorities = SnapshotItems(authorities, nameof(authorities));
        DeclaredApprovers = SnapshotText(declaredApprovers, nameof(declaredApprovers));
        ActionContracts = SnapshotItems(actionContracts, nameof(actionContracts));
    }

    public IReadOnlyList<ActionDomainAuthorityBinding> Authorities { get; }

    public IReadOnlyList<string> DeclaredApprovers { get; }

    public IReadOnlyList<ActionDomainActionContract> ActionContracts { get; }

    private static ImmutableArray<T> SnapshotItems<T>(
        IReadOnlyList<T>? values,
        string parameterName)
        where T : class
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

    private static ImmutableArray<string> SnapshotText(
        IReadOnlyList<string>? values,
        string parameterName)
    {
        if (values is null)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<string>(values.Count);
        foreach (var value in values)
        {
            builder.Add(ActionDomainCatalogGuards.RequireText(value, parameterName));
        }

        return builder.ToImmutable();
    }
}

public sealed record ActionDomainAuthorityBinding
{
    public ActionDomainAuthorityBinding(
        string path = "authority",
        IReadOnlyList<AuthorityKey>? canDecide = null,
        IReadOnlyList<ActionDomainAuthorityOverride>? overrides = null)
    {
        Path = ActionDomainCatalogGuards.RequireText(path, nameof(path));
        CanDecide = SnapshotAuthorityKeys(canDecide, nameof(canDecide));
        Overrides = SnapshotOverrides(overrides, nameof(overrides));
    }

    public string Path { get; }

    public IReadOnlyList<AuthorityKey> CanDecide { get; }

    public IReadOnlyList<ActionDomainAuthorityOverride> Overrides { get; }

    private static ImmutableArray<AuthorityKey> SnapshotAuthorityKeys(
        IReadOnlyList<AuthorityKey>? values,
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

    private static ImmutableArray<ActionDomainAuthorityOverride> SnapshotOverrides(
        IReadOnlyList<ActionDomainAuthorityOverride>? values,
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

public sealed record ActionDomainAuthorityOverride
{
    public ActionDomainAuthorityOverride(
        AuthorityKey key,
        ActionDomainGate gate,
        string? approver = null)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Gate = ActionDomainCatalogGuards.RequireDefined(gate, nameof(gate));
        Approver = approver is null
            ? null
            : ActionDomainCatalogGuards.RequireText(approver, nameof(approver));
    }

    public AuthorityKey Key { get; }

    public ActionDomainGate Gate { get; }

    public string? Approver { get; }
}

public sealed record ActionDomainActionContract
{
    public ActionDomainActionContract(
        ActionDomainActionKind action,
        string selectorAttribute,
        string selectorValue,
        IReadOnlyList<string> providedAttributes)
    {
        Action = ActionDomainCatalogGuards.RequireDefined(action, nameof(action));
        SelectorAttribute = ActionDomainCatalogGuards.RequireText(
            selectorAttribute,
            nameof(selectorAttribute));
        SelectorValue = ActionDomainCatalogGuards.RequireText(selectorValue, nameof(selectorValue));
        ProvidedAttributes = SnapshotAttributes(
            providedAttributes,
            nameof(providedAttributes));

        if (!ProvidedAttributes.Contains(SelectorAttribute, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The selector attribute '{SelectorAttribute}' must be declared as provided."),
                nameof(providedAttributes));
        }
    }

    public ActionDomainActionKind Action { get; }

    public string SelectorAttribute { get; }

    public string SelectorValue { get; }

    public IReadOnlyList<string> ProvidedAttributes { get; }

    public static ActionDomainActionContract ForTool(
        string tool,
        IReadOnlyList<string> providedAttributes) =>
        new(
            ActionDomainActionKind.Tool,
            "tool",
            tool,
            providedAttributes);

    public static ActionDomainActionContract ForOrganizationalMessage(
        string messageType,
        IReadOnlyList<string> providedAttributes) =>
        new(
            ActionDomainActionKind.OrganizationalMessage,
            "message_type",
            messageType,
            providedAttributes);

    private static ImmutableArray<string> SnapshotAttributes(
        IReadOnlyList<string> values,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);

        var builder = ImmutableArray.CreateBuilder<string>(values.Count);
        foreach (var value in values)
        {
            var attribute = ActionDomainCatalogGuards.RequireText(value, parameterName);
            if (attribute.Any(char.IsWhiteSpace))
            {
                throw new ArgumentException(
                    "Provided attribute names cannot contain whitespace.",
                    parameterName);
            }

            builder.Add(attribute);
        }

        return builder.ToImmutable();
    }
}

public static class ActionDomainCatalogValidator
{
    public static ActionDomainCatalogValidationResult Validate(
        ActionDomainCatalog catalog,
        ActionDomainCatalogBinding binding)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(binding);

        var errors = new List<ActionDomainCatalogValidationError>();
        var domainsByKey = ValidateCatalogShape(catalog, errors);

        ValidateAuthorityBindings(binding, domainsByKey, errors);
        ValidatePredicateContracts(catalog, binding, errors);

        return ActionDomainCatalogValidationResult.Create(errors);
    }

    private static Dictionary<string, DomainEntry> ValidateCatalogShape(
        ActionDomainCatalog catalog,
        List<ActionDomainCatalogValidationError> errors)
    {
        var domainsByKey = new Dictionary<string, DomainEntry>(StringComparer.Ordinal);

        if (catalog.Defaults.UnmatchedAction != ActionDomainGate.Escalate)
        {
            errors.Add(new ActionDomainCatalogValidationError(
                "unmatched-action-default-not-escalate",
                "defaults.unmatched_action",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Unmatched action default must be 'escalate', not '{catalog.Defaults.UnmatchedAction}'.")));
        }

        for (var index = 0; index < catalog.Domains.Count; index++)
        {
            var domain = catalog.Domains[index];
            var key = domain.Key.Value;

            if (domainsByKey.TryGetValue(key, out var existing))
            {
                errors.Add(new ActionDomainCatalogValidationError(
                    "duplicate-action-domain-key",
                    $"domains[{index}].key",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Duplicate action-domain key '{key}'; first declared at domains[{existing.Index}].")));
            }
            else
            {
                domainsByKey[key] = new DomainEntry(domain, index);
            }

            if (domain.Match.Count == 0 && domain.Gate != ActionDomainGate.Decide)
            {
                errors.Add(new ActionDomainCatalogValidationError(
                    "trust-key-gate-not-decide",
                    $"domains[{index}].gate",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Trust key '{key}' has no match predicates and must use gate 'decide'.")));
            }
        }

        return domainsByKey;
    }

    private static void ValidateAuthorityBindings(
        ActionDomainCatalogBinding binding,
        IReadOnlyDictionary<string, DomainEntry> domainsByKey,
        List<ActionDomainCatalogValidationError> errors)
    {
        var declaredApprovers = new HashSet<string>(
            binding.DeclaredApprovers,
            StringComparer.Ordinal);

        foreach (var authority in binding.Authorities)
        {
            for (var index = 0; index < authority.CanDecide.Count; index++)
            {
                var key = authority.CanDecide[index].Value;
                var path = $"{authority.Path}.can_decide[{index}]";

                if (!domainsByKey.TryGetValue(key, out var domain))
                {
                    errors.Add(NotFound(path, key));
                    continue;
                }

                if (domain.Domain.Match.Count > 0)
                {
                    errors.Add(new ActionDomainCatalogValidationError(
                        "can-decide-key-has-match",
                        path,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Authority key '{key}' has objective match predicates and cannot be listed in can_decide.")));
                }
            }

            for (var index = 0; index < authority.Overrides.Count; index++)
            {
                var authorityOverride = authority.Overrides[index];
                var key = authorityOverride.Key.Value;
                var overridePath = $"{authority.Path}.overrides[{index}]";

                if (domainsByKey.TryGetValue(key, out var domain))
                {
                    if (domain.Domain.Match.Count == 0)
                    {
                        errors.Add(new ActionDomainCatalogValidationError(
                            "override-key-has-no-match",
                            $"{overridePath}.key",
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"Override for '{key}' targets a trust key without match predicates.")));
                    }

                    if (authorityOverride.Gate < domain.Domain.Gate)
                    {
                        errors.Add(new ActionDomainCatalogValidationError(
                            "override-gate-relaxes-minimum",
                            $"{overridePath}.gate",
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"Override for '{key}' uses gate '{authorityOverride.Gate}' below catalog minimum '{domain.Domain.Gate}'.")));
                    }
                }
                else
                {
                    errors.Add(NotFound($"{overridePath}.key", key));
                }

                if (authorityOverride.Approver is { } approver
                    && !declaredApprovers.Contains(approver))
                {
                    errors.Add(new ActionDomainCatalogValidationError(
                        "override-approver-not-found",
                        $"{overridePath}.approver",
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Override approver '{approver}' does not resolve to a declared approver.")));
                }
            }
        }
    }

    private static void ValidatePredicateContracts(
        ActionDomainCatalog catalog,
        ActionDomainCatalogBinding binding,
        List<ActionDomainCatalogValidationError> errors)
    {
        var contracts = new Dictionary<ContractKey, ActionDomainActionContract>();
        foreach (var contract in binding.ActionContracts)
        {
            var key = new ContractKey(
                contract.Action,
                contract.SelectorAttribute,
                contract.SelectorValue);
            contracts.TryAdd(key, contract);
        }

        for (var domainIndex = 0; domainIndex < catalog.Domains.Count; domainIndex++)
        {
            var domain = catalog.Domains[domainIndex];

            for (var matchIndex = 0; matchIndex < domain.Match.Count; matchIndex++)
            {
                var predicate = domain.Match[matchIndex];
                var selectorAttribute = SelectorAttribute(predicate.Action);
                var path = $"domains[{domainIndex}].match[{matchIndex}]";

                if (!TryGetSelectorValue(predicate, selectorAttribute, out var selectorValue))
                {
                    errors.Add(new ActionDomainCatalogValidationError(
                        "predicate-selector-missing",
                        path,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Match predicate must declare selector attribute '{selectorAttribute}'.")));
                    continue;
                }

                var contractKey = new ContractKey(
                    predicate.Action,
                    selectorAttribute,
                    selectorValue);

                if (!contracts.TryGetValue(contractKey, out var contract))
                {
                    errors.Add(new ActionDomainCatalogValidationError(
                        "action-contract-not-found",
                        $"{path}.{selectorAttribute}",
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"No action contract declares {selectorAttribute} '{selectorValue}' for action '{predicate.Action}'.")));
                    continue;
                }

                var providedAttributes = new HashSet<string>(
                    contract.ProvidedAttributes,
                    StringComparer.Ordinal);
                foreach (var attribute in predicate.Attributes.Keys)
                {
                    if (!providedAttributes.Contains(attribute))
                    {
                        errors.Add(new ActionDomainCatalogValidationError(
                            "predicate-attribute-not-declared",
                            $"{path}.{attribute}",
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"Predicate attribute '{attribute}' is not declared by the action contract for '{selectorValue}'.")));
                    }
                }
            }
        }
    }

    private static string SelectorAttribute(ActionDomainActionKind action) =>
        action switch
        {
            ActionDomainActionKind.Tool => "tool",
            ActionDomainActionKind.OrganizationalMessage => "message_type",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown action kind."),
        };

    private static bool TryGetSelectorValue(
        ActionDomainMatchPredicate predicate,
        string selectorAttribute,
        out string selectorValue)
    {
        selectorValue = string.Empty;
        if (!predicate.Attributes.TryGetValue(selectorAttribute, out var raw))
        {
            return false;
        }

        if (raw is not string value || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        selectorValue = value;
        return true;
    }

    private static ActionDomainCatalogValidationError NotFound(string path, string key) =>
        new(
            "authority-key-not-found",
            path,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Authority key '{key}' does not resolve to the action-domain catalog."));

    private sealed record DomainEntry(ActionDomain Domain, int Index);

    private readonly record struct ContractKey(
        ActionDomainActionKind Action,
        string SelectorAttribute,
        string SelectorValue);
}
