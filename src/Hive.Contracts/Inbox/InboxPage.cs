using System.Text.Json.Serialization;

namespace Hive.Contracts.Inbox;

/// <summary>
/// One cursor-paginated snapshot from the authenticated principal's inbox.
/// </summary>
public sealed record InboxPage
{
    public InboxPage(
        DateTimeOffset generatedAtUtc,
        DateTimeOffset? lastEventAppliedAtUtc,
        int pageSize,
        string? nextCursor,
        IEnumerable<InboxItem> items)
    {
        GeneratedAtUtc = InboxContractGuards.UtcTimestamp(
            generatedAtUtc,
            nameof(generatedAtUtc));
        LastEventAppliedAtUtc = InboxContractGuards.OptionalUtcTimestamp(
            lastEventAppliedAtUtc,
            nameof(lastEventAppliedAtUtc));
        PageSize = InboxContractGuards.PageSize(pageSize, nameof(pageSize));
        NextCursor = InboxContractGuards.OptionalCursor(nextCursor, nameof(nextCursor));
        ArgumentNullException.ThrowIfNull(items);
        Items = items.ToArray();

        if (Items.Any(static item => item is null))
        {
            throw new ArgumentException("Inbox items cannot contain null entries.", nameof(items));
        }

        if (Items.Count > PageSize)
        {
            throw new ArgumentException(
                "An inbox page cannot contain more items than its requested page size.",
                nameof(items));
        }
    }

    [JsonPropertyName("generated_at_utc")]
    public DateTimeOffset GeneratedAtUtc { get; }

    [JsonPropertyName("last_event_applied_at_utc")]
    public DateTimeOffset? LastEventAppliedAtUtc { get; }

    [JsonPropertyName("page_size")]
    public int PageSize { get; }

    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; }

    [JsonPropertyName("items")]
    public IReadOnlyList<InboxItem> Items { get; }
}
