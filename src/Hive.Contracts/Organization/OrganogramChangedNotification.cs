using System.Text.Json.Serialization;

namespace Hive.Contracts.Organization;

public sealed record OrganogramChangedNotification
{
    public OrganogramChangedNotification(
        string organizationId,
        RegistryVersion registry,
        DateTimeOffset changedAtUtc)
    {
        OrganizationId = OrganizationContractGuards.Identifier(
            organizationId,
            nameof(organizationId));
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        ChangedAtUtc = OrganizationContractGuards.UtcTimestamp(
            changedAtUtc,
            nameof(changedAtUtc));
    }

    [JsonPropertyName("organization_id")]
    public string OrganizationId { get; }

    [JsonPropertyName("registry")]
    public RegistryVersion Registry { get; }

    [JsonPropertyName("changed_at_utc")]
    public DateTimeOffset ChangedAtUtc { get; }
}
