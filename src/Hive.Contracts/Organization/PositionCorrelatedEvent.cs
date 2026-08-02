using System.Text.Json.Serialization;

namespace Hive.Contracts.Organization;

public sealed record PositionCorrelatedEvent
{
    public PositionCorrelatedEvent(
        string type,
        Guid threadId,
        DateTimeOffset occurredAtUtc)
    {
        if (threadId == Guid.Empty)
        {
            throw new ArgumentException("Thread identifier cannot be empty.", nameof(threadId));
        }

        Type = OrganizationContractGuards.Identifier(type, nameof(type));
        ThreadId = threadId;
        OccurredAtUtc = OrganizationContractGuards.UtcTimestamp(
            occurredAtUtc,
            nameof(occurredAtUtc));
    }

    [JsonPropertyName("type")]
    public string Type { get; }

    [JsonPropertyName("thread_id")]
    public Guid ThreadId { get; }

    [JsonPropertyName("occurred_at_utc")]
    public DateTimeOffset OccurredAtUtc { get; }
}
