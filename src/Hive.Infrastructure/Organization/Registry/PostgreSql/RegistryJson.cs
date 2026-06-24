using System.Text.Json;
using System.Text.Json.Serialization;

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
}
