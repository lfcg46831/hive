using System.Text.Json.Serialization;

namespace Hive.Contracts.Organization;

public sealed record PositionHierarchy
{
    public PositionHierarchy(
        string? reportsToPositionId,
        IReadOnlyList<string> directSubordinatePositionIds)
    {
        ReportsToPositionId = OrganizationContractGuards.OptionalIdentifier(
            reportsToPositionId,
            nameof(reportsToPositionId));
        DirectSubordinatePositionIds = OrganizationContractGuards.SortedIdentifiers(
            directSubordinatePositionIds,
            nameof(directSubordinatePositionIds));
    }

    [JsonPropertyName("reports_to_position_id")]
    public string? ReportsToPositionId { get; }

    [JsonPropertyName("direct_subordinate_position_ids")]
    public IReadOnlyList<string> DirectSubordinatePositionIds { get; }
}
