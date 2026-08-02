using System.Text.Json.Serialization;

namespace Hive.Contracts.Organization;

public sealed record OrganizationUnit
{
    public OrganizationUnit(
        string id,
        string? name,
        string? parentUnitId,
        string leadershipPositionId)
    {
        Id = OrganizationContractGuards.Identifier(id, nameof(id));
        Name = OrganizationContractGuards.OptionalDisplayName(name, nameof(name));
        ParentUnitId = OrganizationContractGuards.OptionalIdentifier(
            parentUnitId,
            nameof(parentUnitId));
        LeadershipPositionId = OrganizationContractGuards.Identifier(
            leadershipPositionId,
            nameof(leadershipPositionId));
    }

    [JsonPropertyName("id")]
    public string Id { get; }

    [JsonPropertyName("name")]
    public string? Name { get; }

    [JsonPropertyName("parent_unit_id")]
    public string? ParentUnitId { get; }

    [JsonPropertyName("leadership_position_id")]
    public string LeadershipPositionId { get; }
}
