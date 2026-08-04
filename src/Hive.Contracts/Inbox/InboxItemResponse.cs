using System.Text.Json.Serialization;

namespace Hive.Contracts.Inbox;

/// <summary>
/// Snapshot response for one inbox item.
/// </summary>
public sealed record InboxItemResponse
{
    public InboxItemResponse(
        DateTimeOffset generatedAtUtc,
        DateTimeOffset? lastEventAppliedAtUtc,
        InboxItem item)
    {
        GeneratedAtUtc = InboxContractGuards.UtcTimestamp(
            generatedAtUtc,
            nameof(generatedAtUtc));
        LastEventAppliedAtUtc = InboxContractGuards.OptionalUtcTimestamp(
            lastEventAppliedAtUtc,
            nameof(lastEventAppliedAtUtc));
        Item = item ?? throw new ArgumentNullException(nameof(item));
    }

    [JsonPropertyName("generated_at_utc")]
    public DateTimeOffset GeneratedAtUtc { get; }

    [JsonPropertyName("last_event_applied_at_utc")]
    public DateTimeOffset? LastEventAppliedAtUtc { get; }

    [JsonPropertyName("item")]
    public InboxItem Item { get; }
}
