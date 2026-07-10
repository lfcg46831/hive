using System.Collections.Immutable;
using System.Globalization;

namespace Hive.Domain.Governance;

public enum ActionAttributeValueKind
{
    String = 1,
    Boolean = 2,
    Integer = 3,
    Decimal = 4,
}

public enum ActionAttributeSource
{
    Direct = 1,
    Derived = 2,
}

/// <summary>A canonical scalar that may participate in an objective action-domain predicate.</summary>
public sealed record ActionAttributeValue
{
    private ActionAttributeValue(ActionAttributeValueKind kind, string canonicalValue)
    {
        Kind = ActionAttributeContracts.RequireDefined(kind, nameof(kind));
        CanonicalValue = ActionDomainCatalogGuards.RequireText(
            canonicalValue,
            nameof(canonicalValue));
    }

    public ActionAttributeValueKind Kind { get; }

    public string CanonicalValue { get; }

    public static ActionAttributeValue FromString(string value) =>
        new(ActionAttributeValueKind.String, value);

    public static ActionAttributeValue FromBoolean(bool value) =>
        new(
            ActionAttributeValueKind.Boolean,
            value ? bool.TrueString.ToLowerInvariant() : bool.FalseString.ToLowerInvariant());

    public static ActionAttributeValue FromInteger(long value) =>
        new(
            ActionAttributeValueKind.Integer,
            value.ToString(CultureInfo.InvariantCulture));

    public static ActionAttributeValue FromDecimal(decimal value) =>
        new(
            ActionAttributeValueKind.Decimal,
            value.ToString("G29", CultureInfo.InvariantCulture));

    public static bool TryFromScalar(object? value, out ActionAttributeValue? attributeValue)
    {
        switch (value)
        {
            case string text when !string.IsNullOrWhiteSpace(text)
                                  && string.Equals(text, text.Trim(), StringComparison.Ordinal):
                attributeValue = FromString(text);
                return true;
            case bool boolean:
                attributeValue = FromBoolean(boolean);
                return true;
            case byte integer:
                attributeValue = FromInteger(integer);
                return true;
            case sbyte integer:
                attributeValue = FromInteger(integer);
                return true;
            case short integer:
                attributeValue = FromInteger(integer);
                return true;
            case ushort integer:
                attributeValue = FromInteger(integer);
                return true;
            case int integer:
                attributeValue = FromInteger(integer);
                return true;
            case uint integer:
                attributeValue = FromInteger(integer);
                return true;
            case long integer:
                attributeValue = FromInteger(integer);
                return true;
            case ulong integer when integer <= long.MaxValue:
                attributeValue = FromInteger((long)integer);
                return true;
            case decimal number:
                attributeValue = FromDecimal(number);
                return true;
            default:
                attributeValue = null;
                return false;
        }
    }

    public override string ToString() => CanonicalValue;
}

/// <summary>One closed attribute supplied by a concrete action contract.</summary>
public sealed record ActionAttributeDefinition
{
    public ActionAttributeDefinition(
        string name,
        ActionAttributeSource source,
        ActionAttributeValueKind valueKind,
        IReadOnlyList<ActionAttributeValue>? allowedValues = null)
    {
        Name = ActionAttributeContracts.RequireName(name, nameof(name));
        Source = ActionAttributeContracts.RequireDefined(source, nameof(source));
        ValueKind = ActionAttributeContracts.RequireDefined(valueKind, nameof(valueKind));
        AllowedValues = SnapshotAllowedValues(allowedValues, valueKind);
    }

    public string Name { get; }

    public ActionAttributeSource Source { get; }

    public ActionAttributeValueKind ValueKind { get; }

    public IReadOnlyList<ActionAttributeValue> AllowedValues { get; }

    public bool Allows(ActionAttributeValue value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value.Kind == ValueKind
               && (AllowedValues.Count == 0 || AllowedValues.Contains(value));
    }

    public static ActionAttributeDefinition Direct(
        string name,
        ActionAttributeValueKind valueKind,
        IReadOnlyList<ActionAttributeValue>? allowedValues = null) =>
        new(name, ActionAttributeSource.Direct, valueKind, allowedValues);

    public static ActionAttributeDefinition Derived(
        string name,
        ActionAttributeValueKind valueKind,
        IReadOnlyList<ActionAttributeValue>? allowedValues = null) =>
        new(name, ActionAttributeSource.Derived, valueKind, allowedValues);

    private static ImmutableArray<ActionAttributeValue> SnapshotAllowedValues(
        IReadOnlyList<ActionAttributeValue>? values,
        ActionAttributeValueKind expectedKind)
    {
        if (values is null)
        {
            return [];
        }

        var snapshot = values.ToImmutableArray();
        if (snapshot.Any(value => value is null))
        {
            throw new ArgumentException(
                "Allowed values cannot contain null entries.",
                nameof(values));
        }

        if (snapshot.Any(value => value.Kind != expectedKind))
        {
            throw new ArgumentException(
                "Allowed values must use the attribute definition value kind.",
                nameof(values));
        }

        if (snapshot.Distinct().Count() != snapshot.Length)
        {
            throw new ArgumentException(
                "Allowed values cannot contain duplicates.",
                nameof(values));
        }

        return snapshot;
    }
}

/// <summary>Canonical direct facts supplied by an already validated action candidate.</summary>
public sealed record ActionAttributeExtractionRequest
{
    public ActionAttributeExtractionRequest(
        ActionDomainActionKind action,
        string selectorValue,
        IReadOnlyDictionary<string, ActionAttributeValue>? directAttributes = null)
    {
        Action = ActionAttributeContracts.RequireDefined(action, nameof(action));
        SelectorValue = ActionDomainCatalogGuards.RequireText(selectorValue, nameof(selectorValue));
        DirectAttributes = ActionAttributeContracts.SnapshotAttributes(
            directAttributes,
            nameof(directAttributes));
    }

    public ActionDomainActionKind Action { get; }

    public string SelectorValue { get; }

    public IReadOnlyDictionary<string, ActionAttributeValue> DirectAttributes { get; }
}

public sealed record ActionAttributeExtractorOutput
{
    private ActionAttributeExtractorOutput(
        ImmutableSortedDictionary<string, ActionAttributeValue> derivedAttributes,
        ActionAttributeExtractorFailureReason? failureReason)
    {
        DerivedAttributes = derivedAttributes;
        FailureReason = failureReason;
    }

    public bool IsSuccess => FailureReason is null;

    public IReadOnlyDictionary<string, ActionAttributeValue> DerivedAttributes { get; }

    public ActionAttributeExtractorFailureReason? FailureReason { get; }

    public static ActionAttributeExtractorOutput Success(
        IReadOnlyDictionary<string, ActionAttributeValue>? derivedAttributes = null) =>
        new(
            ActionAttributeContracts.SnapshotAttributes(
                derivedAttributes,
                nameof(derivedAttributes)),
            null);

    public static ActionAttributeExtractorOutput Failure(
        ActionAttributeExtractorFailureReason reason) =>
        new(
            ActionAttributeContracts.EmptyAttributes,
            ActionAttributeContracts.RequireDefined(reason, nameof(reason)));
}

/// <summary>
/// Pure, synchronous seam for deriving closed action facts. Implementations capture only immutable
/// connector-instance configuration and perform no I/O.
/// </summary>
public interface IActionAttributeExtractor
{
    ActionAttributeExtractorOutput Extract(ActionAttributeExtractionRequest request);
}

public enum ActionAttributeExtractorFailureReason
{
    InvalidInput = 1,
    ClassificationUnavailable = 2,
    ConfigurationUnavailable = 3,
}

public sealed record ActionAttributeExtractorRegistration
{
    public ActionAttributeExtractorRegistration(
        ActionDomainActionKind action,
        string selectorValue,
        IActionAttributeExtractor extractor)
    {
        Action = ActionAttributeContracts.RequireDefined(action, nameof(action));
        SelectorValue = ActionDomainCatalogGuards.RequireText(selectorValue, nameof(selectorValue));
        Extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
    }

    public ActionDomainActionKind Action { get; }

    public string SelectorValue { get; }

    public IActionAttributeExtractor Extractor { get; }

    public static ActionAttributeExtractorRegistration ForTool(
        string tool,
        IActionAttributeExtractor extractor) =>
        new(ActionDomainActionKind.Tool, tool, extractor);

    public static ActionAttributeExtractorRegistration ForOrganizationalMessage(
        string messageType,
        IActionAttributeExtractor extractor) =>
        new(ActionDomainActionKind.OrganizationalMessage, messageType, extractor);
}

public enum ActionAttributeExtractionFailureKind
{
    ContractMismatch = 1,
    ExtractorRejected = 2,
    ExtractorThrew = 3,
    ContractViolation = 4,
}

public sealed record ActionAttributeExtractionFailure
{
    internal ActionAttributeExtractionFailure(
        ActionAttributeExtractionFailureKind kind,
        string code,
        string? attribute = null)
    {
        Kind = ActionAttributeContracts.RequireDefined(kind, nameof(kind));
        Code = ActionAttributeContracts.RequireCode(code, nameof(code));
        Attribute = attribute is null
            ? null
            : ActionAttributeContracts.RequireName(attribute, nameof(attribute));
    }

    public ActionAttributeExtractionFailureKind Kind { get; }

    public string Code { get; }

    public string? Attribute { get; }
}

public sealed record ActionFacts
{
    internal ActionFacts(
        ActionDomainActionKind action,
        string selectorValue,
        ImmutableSortedDictionary<string, ActionAttributeValue> attributes)
    {
        Action = action;
        SelectorValue = selectorValue;
        Attributes = attributes;
    }

    public ActionDomainActionKind Action { get; }

    public string SelectorValue { get; }

    public IReadOnlyDictionary<string, ActionAttributeValue> Attributes { get; }
}

public sealed record ActionAttributeExtractionResult
{
    private ActionAttributeExtractionResult(
        ActionFacts? facts,
        ActionAttributeExtractionFailure? failure)
    {
        Facts = facts;
        Failure = failure;
    }

    public bool IsSuccess => Facts is not null;

    public ActionFacts? Facts { get; }

    public ActionAttributeExtractionFailure? Failure { get; }

    internal static ActionAttributeExtractionResult Success(ActionFacts facts) =>
        new(facts ?? throw new ArgumentNullException(nameof(facts)), null);

    internal static ActionAttributeExtractionResult Failed(
        ActionAttributeExtractionFailureKind kind,
        string code,
        string? attribute = null) =>
        new(null, new ActionAttributeExtractionFailure(kind, code, attribute));
}

/// <summary>Platform-owned verifier that turns a connector extractor output into trusted facts.</summary>
public static class ActionAttributeExtractorRunner
{
    public static ActionAttributeExtractionResult Extract(
        ActionDomainActionContract contract,
        ActionAttributeExtractorRegistration? registration,
        ActionAttributeExtractionRequest request)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Action != contract.Action
            || !string.Equals(
                request.SelectorValue,
                contract.SelectorValue,
                StringComparison.Ordinal))
        {
            return Failed(
                ActionAttributeExtractionFailureKind.ContractMismatch,
                "action-contract-mismatch");
        }

        var attributes = ImmutableSortedDictionary.CreateBuilder<string, ActionAttributeValue>(
            StringComparer.Ordinal);
        attributes.Add(
            contract.SelectorAttribute,
            ActionAttributeValue.FromString(contract.SelectorValue));

        var directDefinitions = contract.Attributes
            .Where(definition => definition.Source == ActionAttributeSource.Direct
                                 && !string.Equals(
                                     definition.Name,
                                     contract.SelectorAttribute,
                                     StringComparison.Ordinal))
            .OrderBy(definition => definition.Name, StringComparer.Ordinal)
            .ToArray();

        foreach (var (name, value) in request.DirectAttributes)
        {
            if (string.Equals(name, contract.SelectorAttribute, StringComparison.Ordinal))
            {
                return Failed(
                    ActionAttributeExtractionFailureKind.ContractViolation,
                    "direct-attribute-selector-collision",
                    name);
            }

            var definition = contract.FindAttribute(name);
            if (definition is null)
            {
                return Failed(
                    ActionAttributeExtractionFailureKind.ContractViolation,
                    "direct-attribute-not-declared");
            }

            if (definition.Source != ActionAttributeSource.Direct)
            {
                return Failed(
                    ActionAttributeExtractionFailureKind.ContractViolation,
                    "direct-derived-attribute-collision",
                    name);
            }

            if (value.Kind != definition.ValueKind)
            {
                return Failed(
                    ActionAttributeExtractionFailureKind.ContractViolation,
                    "direct-attribute-type-mismatch",
                    name);
            }

            if (!definition.Allows(value))
            {
                return Failed(
                    ActionAttributeExtractionFailureKind.ContractViolation,
                    "direct-attribute-value-not-allowed",
                    name);
            }

            attributes.Add(name, value);
        }

        foreach (var definition in directDefinitions)
        {
            if (!request.DirectAttributes.ContainsKey(definition.Name))
            {
                return Failed(
                    ActionAttributeExtractionFailureKind.ContractViolation,
                    "direct-attribute-missing",
                    definition.Name);
            }
        }

        var derivedDefinitions = contract.Attributes
            .Where(definition => definition.Source == ActionAttributeSource.Derived)
            .OrderBy(definition => definition.Name, StringComparer.Ordinal)
            .ToArray();

        if (derivedDefinitions.Length == 0)
        {
            return registration is null
                ? Success(contract, attributes)
                : Failed(
                    ActionAttributeExtractionFailureKind.ContractMismatch,
                    "action-extractor-unexpected");
        }

        if (registration is null)
        {
            return Failed(
                ActionAttributeExtractionFailureKind.ContractMismatch,
                "action-extractor-missing");
        }

        if (registration.Action != contract.Action
            || !string.Equals(
                registration.SelectorValue,
                contract.SelectorValue,
                StringComparison.Ordinal))
        {
            return Failed(
                ActionAttributeExtractionFailureKind.ContractMismatch,
                "action-extractor-contract-mismatch");
        }

        ActionAttributeExtractorOutput output;
        try
        {
            output = registration.Extractor.Extract(request);
        }
        catch (Exception)
        {
            return Failed(
                ActionAttributeExtractionFailureKind.ExtractorThrew,
                "action-attribute-extractor-threw");
        }

        if (output is null)
        {
            return Failed(
                ActionAttributeExtractionFailureKind.ContractViolation,
                "action-attribute-extractor-returned-null");
        }

        if (!output.IsSuccess)
        {
            return Failed(
                ActionAttributeExtractionFailureKind.ExtractorRejected,
                ExtractorFailureCode(output.FailureReason!.Value));
        }

        foreach (var (name, value) in output.DerivedAttributes)
        {
            var definition = contract.FindAttribute(name);
            if (definition is null)
            {
                return Failed(
                    ActionAttributeExtractionFailureKind.ContractViolation,
                    "derived-attribute-unexpected");
            }

            if (definition.Source != ActionAttributeSource.Derived)
            {
                return Failed(
                    ActionAttributeExtractionFailureKind.ContractViolation,
                    "derived-direct-attribute-collision",
                    name);
            }

            if (value.Kind != definition.ValueKind)
            {
                return Failed(
                    ActionAttributeExtractionFailureKind.ContractViolation,
                    "derived-attribute-type-mismatch",
                    name);
            }

            if (!definition.Allows(value))
            {
                return Failed(
                    ActionAttributeExtractionFailureKind.ContractViolation,
                    "derived-attribute-value-not-allowed",
                    name);
            }

            attributes.Add(name, value);
        }

        foreach (var definition in derivedDefinitions)
        {
            if (!output.DerivedAttributes.ContainsKey(definition.Name))
            {
                return Failed(
                    ActionAttributeExtractionFailureKind.ContractViolation,
                    "derived-attribute-missing",
                    definition.Name);
            }
        }

        return Success(contract, attributes);
    }

    private static ActionAttributeExtractionResult Success(
        ActionDomainActionContract contract,
        ImmutableSortedDictionary<string, ActionAttributeValue>.Builder attributes) =>
        ActionAttributeExtractionResult.Success(
            new ActionFacts(contract.Action, contract.SelectorValue, attributes.ToImmutable()));

    private static ActionAttributeExtractionResult Failed(
        ActionAttributeExtractionFailureKind kind,
        string code,
        string? attribute = null) =>
        ActionAttributeExtractionResult.Failed(kind, code, attribute);

    private static string ExtractorFailureCode(ActionAttributeExtractorFailureReason reason) =>
        reason switch
        {
            ActionAttributeExtractorFailureReason.InvalidInput =>
                "action-attribute-extractor-invalid-input",
            ActionAttributeExtractorFailureReason.ClassificationUnavailable =>
                "action-attribute-extractor-classification-unavailable",
            ActionAttributeExtractorFailureReason.ConfigurationUnavailable =>
                "action-attribute-extractor-configuration-unavailable",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown failure reason."),
        };
}

internal static class ActionAttributeContracts
{
    public static ImmutableSortedDictionary<string, ActionAttributeValue> EmptyAttributes { get; } =
        ImmutableSortedDictionary<string, ActionAttributeValue>.Empty.WithComparers(
            StringComparer.Ordinal);

    public static string RequireName(string value, string parameterName)
    {
        var name = ActionDomainCatalogGuards.RequireText(value, parameterName);
        if (name.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException(
                "Action attribute names cannot contain whitespace.",
                parameterName);
        }

        return name;
    }

    public static string RequireCode(string value, string parameterName)
    {
        var code = ActionDomainCatalogGuards.RequireText(value, parameterName);
        if (code[0] == '-' || code[^1] == '-' || code.Any(character =>
                !(character is >= 'a' and <= 'z'
                  || character is >= '0' and <= '9'
                  || character == '-')))
        {
            throw new ArgumentException(
                "Failure codes must be lowercase kebab-case tokens.",
                parameterName);
        }

        return code;
    }

    public static TEnum RequireDefined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum =>
        ActionDomainCatalogGuards.RequireDefined(value, parameterName);

    public static ImmutableSortedDictionary<string, ActionAttributeValue> SnapshotAttributes(
        IReadOnlyDictionary<string, ActionAttributeValue>? attributes,
        string parameterName)
    {
        if (attributes is null)
        {
            return EmptyAttributes;
        }

        var builder = ImmutableSortedDictionary.CreateBuilder<string, ActionAttributeValue>(
            StringComparer.Ordinal);
        foreach (var (key, value) in attributes)
        {
            var name = RequireName(key, parameterName);
            if (value is null)
            {
                throw new ArgumentException(
                    "Action attribute values cannot be null.",
                    parameterName);
            }

            builder.Add(name, value);
        }

        return builder.ToImmutable();
    }
}
