using System.Text.Json.Serialization;

namespace Hive.Contracts.Organization;

public sealed record OrganizationPositionState
{
    public OrganizationPositionState(
        string positionId,
        PositionOperationalState state,
        long sequence,
        DateTimeOffset updatedAtUtc,
        PositionCorrelatedEvent? lastCorrelatedEvent = null)
    {
        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequence),
                sequence,
                "Position state sequence cannot be negative.");
        }

        PositionId = OrganizationContractGuards.Identifier(positionId, nameof(positionId));
        State = OrganizationContractGuards.DefinedEnum(state, nameof(state));
        Sequence = sequence;
        UpdatedAtUtc = OrganizationContractGuards.UtcTimestamp(
            updatedAtUtc,
            nameof(updatedAtUtc));
        LastCorrelatedEvent = lastCorrelatedEvent;
    }

    [JsonPropertyName("position_id")]
    public string PositionId { get; }

    [JsonPropertyName("state")]
    public PositionOperationalState State { get; }

    [JsonPropertyName("sequence")]
    public long Sequence { get; }

    [JsonPropertyName("updated_at_utc")]
    public DateTimeOffset UpdatedAtUtc { get; }

    [JsonPropertyName("last_correlated_event")]
    public PositionCorrelatedEvent? LastCorrelatedEvent { get; }
}
