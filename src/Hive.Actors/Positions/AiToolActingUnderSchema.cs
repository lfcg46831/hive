using System.Collections;
using System.Collections.Immutable;
using Hive.Domain.Ai;
using Hive.Domain.Governance;

namespace Hive.Actors.Positions;

internal static class AiToolActingUnderSchema
{
    public const string PropertyName = "acting_under";

    private const string PropertiesField = "properties";
    private const string RequiredField = "required";
    private const string TypeField = "type";
    private const string ObjectType = "object";
    private const string StringType = "string";

    public static AiToolDefinition Compose(
        AiToolDefinition source,
        IEnumerable<AuthorityKey> canDecide)
    {
        ArgumentNullException.ThrowIfNull(source);

        var vocabulary = CanonicalVocabulary(canDecide);
        if (vocabulary.IsEmpty)
        {
            throw new InvalidOperationException(
                "An acting_under tool schema requires at least one authorized authority key.");
        }

        var schema = CopyRoot(source.ParametersSchema);
        EnsureObjectRoot(schema, source.Name);

        var properties = CopyProperties(schema, source.Name);
        if (properties.ContainsKey(PropertyName))
        {
            throw Incompatible(
                source.Name,
                $"'{PropertiesField}.{PropertyName}' is reserved by HIVE");
        }

        properties.Add(
            PropertyName,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [TypeField] = StringType,
                ["enum"] = vocabulary.ToArray(),
            });

        schema[PropertiesField] = properties;
        schema[RequiredField] = MergeRequired(schema, source.Name);

        return new AiToolDefinition(
            source.Name,
            source.Description,
            schema);
    }

    public static ImmutableArray<string> CanonicalVocabulary(
        IEnumerable<AuthorityKey> canDecide)
    {
        ArgumentNullException.ThrowIfNull(canDecide);

        var values = new List<string>();
        foreach (var key in canDecide)
        {
            if (key is null)
            {
                throw new ArgumentException(
                    "Authority vocabulary cannot contain null entries.",
                    nameof(canDecide));
            }

            values.Add(key.Value);
        }

        return values
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static Dictionary<string, object?> CopyRoot(
        IReadOnlyDictionary<string, object?> source)
    {
        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in source)
        {
            copy.Add(key, value);
        }

        return copy;
    }

    private static void EnsureObjectRoot(
        IDictionary<string, object?> schema,
        string toolName)
    {
        if (!schema.TryGetValue(TypeField, out var declaredType))
        {
            schema[TypeField] = ObjectType;
            return;
        }

        if (declaredType is not string type ||
            !string.Equals(type, ObjectType, StringComparison.Ordinal))
        {
            throw Incompatible(toolName, $"'{TypeField}' must be '{ObjectType}'");
        }
    }

    private static Dictionary<string, object?> CopyProperties(
        IReadOnlyDictionary<string, object?> schema,
        string toolName)
    {
        if (!schema.TryGetValue(PropertiesField, out var declaredProperties))
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        IEnumerable<KeyValuePair<string, object?>> entries = declaredProperties switch
        {
            IReadOnlyDictionary<string, object?> readOnly => readOnly,
            IDictionary<string, object?> dictionary => dictionary,
            _ => throw Incompatible(toolName, $"'{PropertiesField}' must be an object"),
        };

        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            if (!copy.TryAdd(key, value))
            {
                throw Incompatible(
                    toolName,
                    $"'{PropertiesField}' contains duplicate property '{key}'");
            }
        }

        return copy;
    }

    private static string[] MergeRequired(
        IReadOnlyDictionary<string, object?> schema,
        string toolName)
    {
        if (!schema.TryGetValue(RequiredField, out var declaredRequired))
        {
            return [PropertyName];
        }

        if (declaredRequired is string || declaredRequired is not IEnumerable items)
        {
            throw Incompatible(toolName, $"'{RequiredField}' must be an array of strings");
        }

        var required = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item is not string propertyName || string.IsNullOrWhiteSpace(propertyName))
            {
                throw Incompatible(toolName, $"'{RequiredField}' must contain only property names");
            }

            if (!seen.Add(propertyName))
            {
                throw Incompatible(
                    toolName,
                    $"'{RequiredField}' contains duplicate property '{propertyName}'");
            }

            if (string.Equals(propertyName, PropertyName, StringComparison.Ordinal))
            {
                throw Incompatible(
                    toolName,
                    $"'{RequiredField}' collides with reserved property '{PropertyName}'");
            }

            required.Add(propertyName);
        }

        required.Add(PropertyName);
        return required.ToArray();
    }

    private static InvalidOperationException Incompatible(
        string toolName,
        string reason) =>
        new($"AI tool '{toolName}' has an incompatible parameters schema: {reason}.");
}
