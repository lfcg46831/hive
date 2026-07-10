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
        IReadOnlyList<ActionDomainActionContract>? actionContracts = null,
        IReadOnlyList<ActionAttributeExtractorRegistration>? actionExtractors = null)
    {
        Authorities = SnapshotItems(authorities, nameof(authorities));
        DeclaredApprovers = SnapshotText(declaredApprovers, nameof(declaredApprovers));
        ActionContracts = SnapshotItems(actionContracts, nameof(actionContracts));
        ActionExtractors = SnapshotItems(actionExtractors, nameof(actionExtractors));
    }

    public IReadOnlyList<ActionDomainAuthorityBinding> Authorities { get; }

    public IReadOnlyList<string> DeclaredApprovers { get; }

    public IReadOnlyList<ActionDomainActionContract> ActionContracts { get; }

    public IReadOnlyList<ActionAttributeExtractorRegistration> ActionExtractors { get; }

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
        IReadOnlyList<ActionAttributeDefinition> attributes)
    {
        Action = ActionDomainCatalogGuards.RequireDefined(action, nameof(action));
        SelectorAttribute = ActionDomainCatalogGuards.RequireText(
            selectorAttribute,
            nameof(selectorAttribute));
        SelectorValue = ActionDomainCatalogGuards.RequireText(selectorValue, nameof(selectorValue));
        Attributes = SnapshotAttributes(attributes, nameof(attributes));

        var expectedSelector = SelectorAttributeFor(action);
        if (!string.Equals(SelectorAttribute, expectedSelector, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Action '{action}' must use selector attribute '{expectedSelector}' rather than '{SelectorAttribute}'."),
                nameof(selectorAttribute));
        }

        var duplicate = Attributes
            .GroupBy(attribute => attribute.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Action attribute '{duplicate.Key}' is declared more than once."),
                nameof(attributes));
        }

        var selector = FindAttribute(SelectorAttribute);
        var selectorValueFact = ActionAttributeValue.FromString(SelectorValue);
        if (selector is null
            || selector.Source != ActionAttributeSource.Direct
            || selector.ValueKind != ActionAttributeValueKind.String
            || selector.AllowedValues.Count != 1
            || !selector.AllowedValues.Contains(selectorValueFact))
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Selector attribute '{SelectorAttribute}' must be a direct string fixed to '{SelectorValue}'."),
                nameof(attributes));
        }

        ProvidedAttributes = Attributes
            .Select(attribute => attribute.Name)
            .ToImmutableArray();
    }

    public ActionDomainActionKind Action { get; }

    public string SelectorAttribute { get; }

    public string SelectorValue { get; }

    public IReadOnlyList<ActionAttributeDefinition> Attributes { get; }

    public IReadOnlyList<string> ProvidedAttributes { get; }

    public bool HasDerivedAttributes =>
        Attributes.Any(attribute => attribute.Source == ActionAttributeSource.Derived);

    public ActionAttributeDefinition? FindAttribute(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return Attributes.FirstOrDefault(
            attribute => string.Equals(attribute.Name, name, StringComparison.Ordinal));
    }

    public static ActionDomainActionContract ForTool(
        string tool,
        IReadOnlyList<ActionAttributeDefinition>? attributes = null) =>
        new(
            ActionDomainActionKind.Tool,
            "tool",
            tool,
            WithSelector("tool", tool, attributes));

    public static ActionDomainActionContract ForOrganizationalMessage(
        string messageType,
        IReadOnlyList<ActionAttributeDefinition>? attributes = null) =>
        new(
            ActionDomainActionKind.OrganizationalMessage,
            "message_type",
            messageType,
            WithSelector("message_type", messageType, attributes));

    private static ImmutableArray<ActionAttributeDefinition> SnapshotAttributes(
        IReadOnlyList<ActionAttributeDefinition> values,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);

        var snapshot = values.ToImmutableArray();
        if (snapshot.Any(value => value is null))
        {
            throw new ArgumentException(
                "Action attributes cannot contain null entries.",
                parameterName);
        }

        return snapshot;
    }

    private static IReadOnlyList<ActionAttributeDefinition> WithSelector(
        string selectorAttribute,
        string selectorValue,
        IReadOnlyList<ActionAttributeDefinition>? attributes)
    {
        var selector = ActionAttributeDefinition.Direct(
            selectorAttribute,
            ActionAttributeValueKind.String,
            [ActionAttributeValue.FromString(selectorValue)]);

        return attributes is null ? [selector] : [selector, .. attributes];
    }

    internal static string SelectorAttributeFor(ActionDomainActionKind action) =>
        action switch
        {
            ActionDomainActionKind.Tool => "tool",
            ActionDomainActionKind.OrganizationalMessage => "message_type",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown action kind."),
        };
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
        var contracts = new Dictionary<ContractKey, ActionContractEntry>();
        for (var index = 0; index < binding.ActionContracts.Count; index++)
        {
            var contract = binding.ActionContracts[index];
            var key = new ContractKey(
                contract.Action,
                contract.SelectorAttribute,
                contract.SelectorValue);
            if (!contracts.TryAdd(key, new ActionContractEntry(contract, index)))
            {
                errors.Add(new ActionDomainCatalogValidationError(
                    "duplicate-action-contract",
                    $"action_contracts[{index}]",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Action contract for {contract.SelectorAttribute} '{contract.SelectorValue}' is declared more than once.")));
            }
        }

        var extractors = new Dictionary<ContractKey, ActionExtractorEntry>();
        for (var index = 0; index < binding.ActionExtractors.Count; index++)
        {
            var registration = binding.ActionExtractors[index];
            var selectorAttribute = SelectorAttribute(registration.Action);
            var key = new ContractKey(
                registration.Action,
                selectorAttribute,
                registration.SelectorValue);
            if (!extractors.TryAdd(key, new ActionExtractorEntry(registration, index)))
            {
                errors.Add(new ActionDomainCatalogValidationError(
                    "duplicate-action-extractor",
                    $"action_extractors[{index}]",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Action extractor for {selectorAttribute} '{registration.SelectorValue}' is registered more than once.")));
            }
        }

        foreach (var (key, entry) in contracts)
        {
            var hasExtractor = extractors.ContainsKey(key);
            if (entry.Contract.HasDerivedAttributes && !hasExtractor)
            {
                errors.Add(new ActionDomainCatalogValidationError(
                    "action-contract-extractor-missing",
                    $"action_contracts[{entry.Index}]",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Action contract for {key.SelectorAttribute} '{key.SelectorValue}' declares derived attributes but has no extractor.")));
            }
            else if (!entry.Contract.HasDerivedAttributes && hasExtractor)
            {
                errors.Add(new ActionDomainCatalogValidationError(
                    "action-contract-extractor-unexpected",
                    $"action_contracts[{entry.Index}]",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Action contract for {key.SelectorAttribute} '{key.SelectorValue}' has no derived attributes and must not register an extractor.")));
            }
        }

        foreach (var (key, entry) in extractors)
        {
            if (!contracts.ContainsKey(key))
            {
                errors.Add(new ActionDomainCatalogValidationError(
                    "action-extractor-contract-not-found",
                    $"action_extractors[{entry.Index}]",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Action extractor for {key.SelectorAttribute} '{key.SelectorValue}' has no action contract.")));
            }
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

                if (!contracts.TryGetValue(contractKey, out var contractEntry))
                {
                    errors.Add(new ActionDomainCatalogValidationError(
                        "action-contract-not-found",
                        $"{path}.{selectorAttribute}",
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"No action contract declares {selectorAttribute} '{selectorValue}' for action '{predicate.Action}'.")));
                    continue;
                }

                foreach (var (attribute, rawValue) in predicate.Attributes)
                {
                    var definition = contractEntry.Contract.FindAttribute(attribute);
                    if (definition is null)
                    {
                        errors.Add(new ActionDomainCatalogValidationError(
                            "predicate-attribute-not-declared",
                            $"{path}.{attribute}",
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"Predicate attribute '{attribute}' is not declared by the action contract for '{selectorValue}'.")));
                        continue;
                    }

                    if (!ActionAttributeValue.TryFromScalar(rawValue, out var value)
                        || value!.Kind != definition.ValueKind)
                    {
                        errors.Add(new ActionDomainCatalogValidationError(
                            "predicate-attribute-type-mismatch",
                            $"{path}.{attribute}",
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"Predicate attribute '{attribute}' does not use the declared value kind '{definition.ValueKind}'.")));
                        continue;
                    }

                    if (!definition.Allows(value))
                    {
                        errors.Add(new ActionDomainCatalogValidationError(
                            "predicate-attribute-value-not-allowed",
                            $"{path}.{attribute}",
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"Predicate attribute '{attribute}' uses a value outside the action contract's allowed values.")));
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

    private sealed record ActionContractEntry(ActionDomainActionContract Contract, int Index);

    private sealed record ActionExtractorEntry(
        ActionAttributeExtractorRegistration Registration,
        int Index);

    private readonly record struct ContractKey(
        ActionDomainActionKind Action,
        string SelectorAttribute,
        string SelectorValue);
}
