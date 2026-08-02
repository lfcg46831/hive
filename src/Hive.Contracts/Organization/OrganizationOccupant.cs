using System.Text.Json.Serialization;

namespace Hive.Contracts.Organization;

public sealed record OrganizationOccupant
{
    public OrganizationOccupant(string? id, OrganizationOccupantType type)
    {
        Id = OrganizationContractGuards.OptionalIdentifier(id, nameof(id));
        Type = OrganizationContractGuards.DefinedEnum(type, nameof(type));
    }

    [JsonPropertyName("id")]
    public string? Id { get; }

    [JsonPropertyName("type")]
    public OrganizationOccupantType Type { get; }
}
