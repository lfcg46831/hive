using System.Text.Json.Serialization;

namespace Hive.Contracts.Organization;

public sealed record OrganizationSummary
{
    public OrganizationSummary(
        string id,
        string? name,
        string rootUnitId,
        string rootPositionId)
    {
        Id = OrganizationContractGuards.Identifier(id, nameof(id));
        Name = OrganizationContractGuards.OptionalDisplayName(name, nameof(name));
        RootUnitId = OrganizationContractGuards.Identifier(
            rootUnitId,
            nameof(rootUnitId));
        RootPositionId = OrganizationContractGuards.Identifier(
            rootPositionId,
            nameof(rootPositionId));
    }

    [JsonPropertyName("id")]
    public string Id { get; }

    [JsonPropertyName("name")]
    public string? Name { get; }

    [JsonPropertyName("root_unit_id")]
    public string RootUnitId { get; }

    [JsonPropertyName("root_position_id")]
    public string RootPositionId { get; }
}
