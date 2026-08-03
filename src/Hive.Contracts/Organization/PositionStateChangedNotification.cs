using System.Text.Json.Serialization;

namespace Hive.Contracts.Organization;

public sealed record PositionStateChangedNotification
{
    public PositionStateChangedNotification(
        string organizationId,
        OrganizationPositionState state)
    {
        OrganizationId = OrganizationContractGuards.Identifier(
            organizationId,
            nameof(organizationId));
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    [JsonPropertyName("organization_id")]
    public string OrganizationId { get; }

    [JsonPropertyName("state")]
    public OrganizationPositionState State { get; }
}
