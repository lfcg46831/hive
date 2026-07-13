using System.Text.Json;
using System.Text.Json.Serialization;
using Hive.Domain.Governance;

namespace Hive.Infrastructure.Organization.Registry.PostgreSql;

internal static class RegistryJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new InvalidDataException(
            $"Registry JSONB value could not be deserialized as {typeof(T).Name}.");

    public static ActionDomainCatalog DeserializeActionDomainCatalog(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var version = root.GetProperty("version").GetInt32();
        var unmatched = Enum.Parse<ActionDomainGate>(
            root.GetProperty("defaults").GetProperty("unmatchedAction").GetString()!,
            ignoreCase: true);
        var domains = root.GetProperty("domains").EnumerateArray().Select(domain =>
            new ActionDomain(
                AuthorityKey.From(domain.GetProperty("key").GetProperty("value").GetString()!),
                domain.GetProperty("description").GetString()!,
                Enum.Parse<ActionDomainGate>(domain.GetProperty("gate").GetString()!, ignoreCase: true),
                domain.GetProperty("match").EnumerateArray().Select(predicate =>
                    new ActionDomainMatchPredicate(
                        Enum.Parse<ActionDomainActionKind>(
                            predicate.GetProperty("action").GetString()!,
                            ignoreCase: true),
                        predicate.GetProperty("attributes").EnumerateObject().ToDictionary(
                            property => property.Name,
                            property => Scalar(property.Value),
                            StringComparer.Ordinal))).ToArray())).ToArray();

        return new ActionDomainCatalog(
            version,
            new ActionDomainCatalogDefaults(unmatched),
            domains);
    }

    private static object Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
        _ => throw new InvalidDataException("Action-domain predicate attributes must be JSON scalars."),
    };
}
