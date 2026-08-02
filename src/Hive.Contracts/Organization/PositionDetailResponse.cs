using System.Text.Json.Serialization;

namespace Hive.Contracts.Organization;

public sealed record PositionDetailResponse
{
    public PositionDetailResponse(
        RegistryVersion registry,
        DateTimeOffset generatedAtUtc,
        OrganizationPosition position)
    {
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        GeneratedAtUtc = OrganizationContractGuards.UtcTimestamp(
            generatedAtUtc,
            nameof(generatedAtUtc));
        Position = position ?? throw new ArgumentNullException(nameof(position));
    }

    [JsonPropertyName("registry")]
    public RegistryVersion Registry { get; }

    [JsonPropertyName("generated_at_utc")]
    public DateTimeOffset GeneratedAtUtc { get; }

    [JsonPropertyName("position")]
    public OrganizationPosition Position { get; }
}
