using System.Globalization;
using Hive.Domain.Governance;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Hive.Infrastructure.Governance;

/// <summary>
/// Parses an action-domain catalog YAML document into the typed governance model.
/// </summary>
public sealed class ActionDomainCatalogParser
{
    private const string RootPath = "$";

    private static readonly string[] NullScalars = ["null", "Null", "NULL", "~"];

    public ActionDomainCatalogParseResult ParseFile(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        var yaml = File.ReadAllText(filePath);
        return Parse(yaml, filePath);
    }

    public ActionDomainCatalogParseResult Parse(string yaml, string filePath)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(filePath);

        var context = new ParseContext(filePath);

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException exception)
        {
            context.Add(
                RootPath,
                $"invalid YAML: {CleanMessage(exception.Message)}",
                (int)exception.Start.Line,
                (int)exception.Start.Column);
            return ActionDomainCatalogParseResult.Failure(context.Errors);
        }

        if (stream.Documents.Count == 0 ||
            stream.Documents[0].RootNode is null ||
            IsNull(stream.Documents[0].RootNode))
        {
            context.Add(RootPath, "the document is empty; 'version', 'defaults' and 'domains' are required.", 1, 1);
            return ActionDomainCatalogParseResult.Failure(context.Errors);
        }

        if (stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            context.AddAt(
                stream.Documents[0].RootNode,
                RootPath,
                "the document root must be a mapping with 'version', 'defaults' and 'domains' blocks.");
            return ActionDomainCatalogParseResult.Failure(context.Errors);
        }

        var catalog = ReadCatalog(root, context);

        if (catalog is null || context.Errors.Count > 0)
        {
            return ActionDomainCatalogParseResult.Failure(context.Errors);
        }

        return ActionDomainCatalogParseResult.Success(catalog);
    }

    private static ActionDomainCatalog? ReadCatalog(YamlMappingNode root, ParseContext context)
    {
        var version = RequirePositiveInt(root, "version", RootPath, context);
        var defaults = ReadDefaults(root, context);
        var domains = ReadDomains(root, context);

        if (version is null || defaults is null || domains is null)
        {
            return null;
        }

        return new ActionDomainCatalog(version.Value, defaults, domains);
    }

    private static ActionDomainCatalogDefaults? ReadDefaults(YamlMappingNode root, ParseContext context)
    {
        var node = Child(root, "defaults");
        if (node is null)
        {
            context.AddAt(root, "defaults", "required block 'defaults' is missing.");
            return null;
        }

        if (node is not YamlMappingNode defaults)
        {
            context.AddAt(node, "defaults", "block 'defaults' must be a mapping.");
            return null;
        }

        var unmatchedActionNode = Child(defaults, "unmatched_action");
        var unmatchedActionValue = RequireScalar(defaults, "unmatched_action", "defaults", context);
        var unmatchedAction = unmatchedActionValue is null
            ? null
            : ReadGate(unmatchedActionValue, unmatchedActionNode!, "defaults.unmatched_action", context);

        return unmatchedAction is null ? null : new ActionDomainCatalogDefaults(unmatchedAction.Value);
    }

    private static IReadOnlyList<ActionDomain>? ReadDomains(YamlMappingNode root, ParseContext context)
    {
        var node = Child(root, "domains");
        if (node is null)
        {
            context.AddAt(root, "domains", "required block 'domains' is missing.");
            return null;
        }

        if (node is not YamlSequenceNode sequence)
        {
            context.AddAt(node, "domains", "block 'domains' must be a sequence.");
            return null;
        }

        var domains = new List<ActionDomain>(sequence.Children.Count);
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            var path = $"domains[{index}]";
            if (sequence.Children[index] is not YamlMappingNode entry)
            {
                context.AddAt(sequence.Children[index], path, "each domain must be a mapping.");
                continue;
            }

            var domain = ReadDomain(entry, path, context);
            if (domain is not null)
            {
                domains.Add(domain);
            }
        }

        return domains;
    }

    private static ActionDomain? ReadDomain(YamlMappingNode domain, string path, ParseContext context)
    {
        var keyNode = Child(domain, "key");
        var gateNode = Child(domain, "gate");
        var keyValue = RequireScalar(domain, "key", path, context);
        var description = RequireTextScalar(domain, "description", path, context);
        var gateValue = RequireScalar(domain, "gate", path, context);
        var match = ReadMatch(domain, path, context);

        var key = keyValue is null ? null : ReadAuthorityKey(keyValue, keyNode!, $"{path}.key", context);
        var gate = gateValue is null ? null : ReadGate(gateValue, gateNode!, $"{path}.gate", context);

        if (key is null || description is null || gate is null || match is null)
        {
            return null;
        }

        try
        {
            return new ActionDomain(key, description, gate.Value, match);
        }
        catch (ArgumentException exception)
        {
            context.AddAt(domain, path, $"invalid action domain: {exception.Message}");
            return null;
        }
    }

    private static IReadOnlyList<ActionDomainMatchPredicate>? ReadMatch(
        YamlMappingNode domain,
        string path,
        ParseContext context)
    {
        var fieldPath = $"{path}.match";
        var node = Child(domain, "match");
        if (node is null || IsNull(node))
        {
            return Array.Empty<ActionDomainMatchPredicate>();
        }

        if (node is not YamlSequenceNode sequence)
        {
            context.AddAt(node, fieldPath, "field 'match' must be a sequence.");
            return null;
        }

        var predicates = new List<ActionDomainMatchPredicate>(sequence.Children.Count);
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            var predicatePath = $"{fieldPath}[{index}]";
            if (sequence.Children[index] is not YamlMappingNode entry)
            {
                context.AddAt(sequence.Children[index], predicatePath, "each match predicate must be a mapping.");
                continue;
            }

            var predicate = ReadPredicate(entry, predicatePath, context);
            if (predicate is not null)
            {
                predicates.Add(predicate);
            }
        }

        return predicates;
    }

    private static ActionDomainMatchPredicate? ReadPredicate(
        YamlMappingNode predicate,
        string path,
        ParseContext context)
    {
        var actionNode = Child(predicate, "action");
        var actionValue = RequireScalar(predicate, "action", path, context);
        var action = actionValue is null ? null : ReadAction(actionValue, actionNode!, $"{path}.action", context);
        var attributes = ReadAttributes(predicate, path, context);

        if (action is null || attributes is null)
        {
            return null;
        }

        try
        {
            return new ActionDomainMatchPredicate(action.Value, attributes);
        }
        catch (ArgumentException exception)
        {
            context.AddAt(predicate, path, $"invalid match predicate: {exception.Message}");
            return null;
        }
    }

    private static IReadOnlyDictionary<string, object>? ReadAttributes(
        YamlMappingNode predicate,
        string path,
        ParseContext context)
    {
        var attributes = new Dictionary<string, object>(StringComparer.Ordinal);
        var ok = true;

        foreach (var pair in predicate.Children)
        {
            if (pair.Key is not YamlScalarNode keyNode)
            {
                context.AddAt(pair.Key, path, "match predicate attribute keys must be scalar strings.");
                ok = false;
                continue;
            }

            var key = keyNode.Value ?? string.Empty;
            if (string.Equals(key, "action", StringComparison.Ordinal))
            {
                continue;
            }

            var attributePath = AttributePath(path, key);
            if (string.IsNullOrWhiteSpace(key))
            {
                context.AddAt(
                    keyNode,
                    attributePath,
                    "match predicate attribute key cannot be empty or whitespace.");
                ok = false;
                continue;
            }

            if (key.Any(char.IsWhiteSpace))
            {
                context.AddAt(
                    keyNode,
                    attributePath,
                    "match predicate attribute key cannot contain whitespace.");
                ok = false;
                continue;
            }

            if (IsNull(pair.Value))
            {
                context.AddAt(pair.Value, attributePath, "match predicate attributes must not be null.");
                ok = false;
                continue;
            }

            if (pair.Value is not YamlScalarNode valueNode)
            {
                context.AddAt(pair.Value, attributePath, "match predicate attributes must be scalar values.");
                ok = false;
                continue;
            }

            var (valueOk, value) = ReadScalarAttribute(valueNode, attributePath, context);
            if (!valueOk)
            {
                ok = false;
                continue;
            }

            attributes[key] = value!;
        }

        return ok ? attributes : null;
    }

    private static (bool Ok, object? Value) ReadScalarAttribute(
        YamlScalarNode valueNode,
        string path,
        ParseContext context)
    {
        var value = valueNode.Value ?? string.Empty;

        if (valueNode.Style is ScalarStyle.SingleQuoted
            or ScalarStyle.DoubleQuoted
            or ScalarStyle.Literal
            or ScalarStyle.Folded)
        {
            return ReadTextAttribute(valueNode, value, path, context);
        }

        if (bool.TryParse(value, out var boolean))
        {
            return (true, boolean);
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return (true, integer);
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longInteger))
        {
            return (true, longInteger);
        }

        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
        {
            return (true, number);
        }

        return ReadTextAttribute(valueNode, value, path, context);
    }

    private static (bool Ok, object? Value) ReadTextAttribute(
        YamlScalarNode valueNode,
        string value,
        string path,
        ParseContext context)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            context.AddAt(
                valueNode,
                path,
                "match predicate attribute value cannot be empty or whitespace.");
            return (false, null);
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            context.AddAt(
                valueNode,
                path,
                "match predicate attribute value cannot contain leading or trailing whitespace.");
            return (false, null);
        }

        return (true, value);
    }

    private static AuthorityKey? ReadAuthorityKey(string value, YamlNode node, string path, ParseContext context)
    {
        try
        {
            return AuthorityKey.From(value);
        }
        catch (ArgumentException exception)
        {
            context.AddAt(node, path, $"invalid authority key: {exception.Message}");
            return null;
        }
    }

    private static ActionDomainGate? ReadGate(string value, YamlNode node, string path, ParseContext context)
    {
        switch (value)
        {
            case "decide":
                return ActionDomainGate.Decide;
            case "escalate":
                return ActionDomainGate.Escalate;
            case "human-approval":
                return ActionDomainGate.HumanApproval;
            default:
                context.AddAt(node, path, $"unknown gate '{value}'; expected 'decide', 'escalate' or 'human-approval'.");
                return null;
        }
    }

    private static ActionDomainActionKind? ReadAction(string value, YamlNode node, string path, ParseContext context)
    {
        switch (value)
        {
            case "tool":
                return ActionDomainActionKind.Tool;
            case "organizational-message":
                return ActionDomainActionKind.OrganizationalMessage;
            default:
                context.AddAt(node, path, $"unknown action kind '{value}'; expected 'tool' or 'organizational-message'.");
                return null;
        }
    }

    private static int? RequirePositiveInt(YamlMappingNode map, string key, string path, ParseContext context)
    {
        var fieldPath = FieldPath(path, key);
        var raw = RequireScalar(map, key, path, context);
        if (raw is null)
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            context.AddAt(Child(map, key)!, fieldPath, $"field '{key}' must be a positive integer; got '{raw}'.");
            return null;
        }

        if (value <= 0)
        {
            context.AddAt(Child(map, key)!, fieldPath, $"field '{key}' must be a positive integer; got '{raw}'.");
            return null;
        }

        return value;
    }

    private static string? RequireScalar(YamlMappingNode map, string key, string path, ParseContext context)
    {
        var fieldPath = FieldPath(path, key);
        var node = Child(map, key);
        if (node is null)
        {
            context.AddAt(map, fieldPath, $"required field '{key}' is missing.");
            return null;
        }

        if (IsNull(node))
        {
            context.AddAt(node, fieldPath, $"required field '{key}' must not be null.");
            return null;
        }

        if (node is not YamlScalarNode scalar)
        {
            context.AddAt(node, fieldPath, $"field '{key}' must be a scalar value.");
            return null;
        }

        return scalar.Value;
    }

    private static YamlNode? Child(YamlMappingNode map, string key)
    {
        foreach (var pair in map.Children)
        {
            if (pair.Key is YamlScalarNode scalar && string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static string FieldPath(string path, string key) =>
        string.Equals(path, RootPath, StringComparison.Ordinal) ? key : $"{path}.{key}";

    private static string AttributePath(string path, string key) =>
        string.IsNullOrEmpty(key) ? $"{path}.<empty>" : $"{path}.{key}";

    private static string? RequireTextScalar(YamlMappingNode map, string key, string path, ParseContext context)
    {
        var fieldPath = FieldPath(path, key);
        var node = Child(map, key);
        var value = RequireScalar(map, key, path, context);
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            context.AddAt(node!, fieldPath, $"field '{key}' cannot be empty or whitespace.");
            return null;
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            context.AddAt(node!, fieldPath, $"field '{key}' cannot contain leading or trailing whitespace.");
            return null;
        }

        return value;
    }

    private static bool IsNull(YamlNode node)
    {
        if (node is not YamlScalarNode scalar || scalar.Style != ScalarStyle.Plain)
        {
            return false;
        }

        var value = scalar.Value;
        return string.IsNullOrEmpty(value) || Array.IndexOf(NullScalars, value) >= 0;
    }

    private static string CleanMessage(string message)
    {
        var separator = message.LastIndexOf("): ", StringComparison.Ordinal);
        var tail = separator >= 0 ? message[(separator + 3)..] : message;
        return tail.Trim();
    }

    private sealed class ParseContext
    {
        private readonly List<ActionDomainCatalogParseError> _errors = new();

        public ParseContext(string filePath) => FilePath = filePath;

        public string FilePath { get; }

        public IReadOnlyList<ActionDomainCatalogParseError> Errors => _errors;

        public void Add(string fieldPath, string message, int? line = null, int? column = null) =>
            _errors.Add(new ActionDomainCatalogParseError(FilePath, fieldPath, message, line, column));

        public void AddAt(YamlNode node, string fieldPath, string message)
        {
            int? line = node is null ? null : (int)node.Start.Line;
            int? column = node is null ? null : (int)node.Start.Column;
            _errors.Add(new ActionDomainCatalogParseError(FilePath, fieldPath, message, line, column));
        }
    }
}
