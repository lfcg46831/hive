using System.Text.Json.Serialization;

namespace Hive.Contracts.Organization;

/// <summary>
/// Public read-only representation of a complete organization tree or a unit subtree.
/// </summary>
public sealed record OrganogramResponse
{
    public OrganogramResponse(
        RegistryVersion registry,
        DateTimeOffset generatedAtUtc,
        string rootUnitId,
        OrganizationSummary organization,
        IReadOnlyList<OrganizationUnit> units,
        IReadOnlyList<OrganizationPosition> positions)
    {
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        GeneratedAtUtc = OrganizationContractGuards.UtcTimestamp(
            generatedAtUtc,
            nameof(generatedAtUtc));
        RootUnitId = OrganizationContractGuards.Identifier(rootUnitId, nameof(rootUnitId));
        Organization = organization ?? throw new ArgumentNullException(nameof(organization));
        Units = OrganizationContractGuards.SortedSnapshot(
            units,
            unit => unit.Id,
            nameof(units));
        Positions = OrganizationContractGuards.SortedSnapshot(
            positions,
            position => position.Id,
            nameof(positions));
    }

    [JsonPropertyName("registry")]
    public RegistryVersion Registry { get; }

    [JsonPropertyName("generated_at_utc")]
    public DateTimeOffset GeneratedAtUtc { get; }

    [JsonPropertyName("root_unit_id")]
    public string RootUnitId { get; }

    [JsonPropertyName("organization")]
    public OrganizationSummary Organization { get; }

    [JsonPropertyName("units")]
    public IReadOnlyList<OrganizationUnit> Units { get; }

    [JsonPropertyName("positions")]
    public IReadOnlyList<OrganizationPosition> Positions { get; }
}
