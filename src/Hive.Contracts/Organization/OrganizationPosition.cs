using System.Text.Json.Serialization;

namespace Hive.Contracts.Organization;

public sealed record OrganizationPosition
{
    public OrganizationPosition(
        string id,
        string? name,
        string unitId,
        OrganizationOccupant occupant,
        PositionHierarchy hierarchy,
        OrganizationPositionState operationalState)
    {
        Id = OrganizationContractGuards.Identifier(id, nameof(id));
        Name = OrganizationContractGuards.OptionalDisplayName(name, nameof(name));
        UnitId = OrganizationContractGuards.Identifier(unitId, nameof(unitId));
        Occupant = occupant ?? throw new ArgumentNullException(nameof(occupant));
        Hierarchy = hierarchy ?? throw new ArgumentNullException(nameof(hierarchy));
        OperationalState = operationalState
            ?? throw new ArgumentNullException(nameof(operationalState));

        if (!string.Equals(Id, OperationalState.PositionId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Embedded operational state must belong to the same position.",
                nameof(operationalState));
        }
    }

    [JsonPropertyName("id")]
    public string Id { get; }

    [JsonPropertyName("name")]
    public string? Name { get; }

    [JsonPropertyName("unit_id")]
    public string UnitId { get; }

    [JsonPropertyName("occupant")]
    public OrganizationOccupant Occupant { get; }

    [JsonPropertyName("hierarchy")]
    public PositionHierarchy Hierarchy { get; }

    [JsonPropertyName("operational_state")]
    public OrganizationPositionState OperationalState { get; }
}
