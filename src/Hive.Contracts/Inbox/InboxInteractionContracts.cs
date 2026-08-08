using System.Text.Json.Serialization;

namespace Hive.Contracts.Inbox;

/// <summary>
/// Plain-text draft input for one inbox item. A null body starts a response without saving text;
/// an empty body clears the current draft while keeping the response in progress.
/// </summary>
public sealed record InboxDraftRequest
{
    public InboxDraftRequest(string? body)
    {
        Body = body;
    }

    [JsonPropertyName("body")]
    public string? Body { get; }
}

/// <summary>Current principal-owned interaction state after one public inbox action.</summary>
public sealed record InboxInteractionResponse
{
    public InboxInteractionResponse(
        DateTimeOffset generatedAtUtc,
        DateTimeOffset? lastEventAppliedAtUtc,
        string itemId,
        InboxReadState readState,
        InboxResponseState responseState,
        string? draftText,
        DateTimeOffset interactionUpdatedAtUtc)
    {
        GeneratedAtUtc = InboxContractGuards.UtcTimestamp(
            generatedAtUtc,
            nameof(generatedAtUtc));
        LastEventAppliedAtUtc = InboxContractGuards.OptionalUtcTimestamp(
            lastEventAppliedAtUtc,
            nameof(lastEventAppliedAtUtc));
        ItemId = InboxContractGuards.ItemIdentifier(itemId, nameof(itemId));
        ReadState = InboxContractGuards.DefinedEnum(readState, nameof(readState));
        ResponseState = InboxContractGuards.DefinedEnum(responseState, nameof(responseState));
        DraftText = draftText;
        InteractionUpdatedAtUtc = InboxContractGuards.UtcTimestamp(
            interactionUpdatedAtUtc,
            nameof(interactionUpdatedAtUtc));
    }

    [JsonPropertyName("generated_at_utc")]
    public DateTimeOffset GeneratedAtUtc { get; }

    [JsonPropertyName("last_event_applied_at_utc")]
    public DateTimeOffset? LastEventAppliedAtUtc { get; }

    [JsonPropertyName("item_id")]
    public string ItemId { get; }

    [JsonPropertyName("read_state")]
    public InboxReadState ReadState { get; }

    [JsonPropertyName("response_state")]
    public InboxResponseState ResponseState { get; }

    [JsonPropertyName("draft_text")]
    public string? DraftText { get; }

    [JsonPropertyName("interaction_updated_at_utc")]
    public DateTimeOffset InteractionUpdatedAtUtc { get; }
}
