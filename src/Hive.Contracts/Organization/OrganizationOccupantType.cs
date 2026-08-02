using System.Text.Json.Serialization;

namespace Hive.Contracts.Organization;

[JsonConverter(typeof(JsonStringEnumConverter<OrganizationOccupantType>))]
public enum OrganizationOccupantType
{
    AiAgent,
    Human,
}
