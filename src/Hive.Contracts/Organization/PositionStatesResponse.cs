using System.Text.Json.Serialization;

namespace Hive.Contracts.Organization;

public sealed record PositionStatesResponse
{
    public PositionStatesResponse(
        RegistryVersion registry,
        DateTimeOffset generatedAtUtc,
        DateTimeOffset? lastEventAppliedAtUtc,
        IReadOnlyList<OrganizationPositionState> states)
    {
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        GeneratedAtUtc = OrganizationContractGuards.UtcTimestamp(
            generatedAtUtc,
            nameof(generatedAtUtc));
        LastEventAppliedAtUtc = OrganizationContractGuards.OptionalUtcTimestamp(
            lastEventAppliedAtUtc,
            nameof(lastEventAppliedAtUtc));
        States = OrganizationContractGuards.SortedSnapshot(
            states,
            state => state.PositionId,
            nameof(states));
    }

    [JsonPropertyName("registry")]
    public RegistryVersion Registry { get; }

    [JsonPropertyName("generated_at_utc")]
    public DateTimeOffset GeneratedAtUtc { get; }

    [JsonPropertyName("last_event_applied_at_utc")]
    public DateTimeOffset? LastEventAppliedAtUtc { get; }

    [JsonPropertyName("states")]
    public IReadOnlyList<OrganizationPositionState> States { get; }
}
