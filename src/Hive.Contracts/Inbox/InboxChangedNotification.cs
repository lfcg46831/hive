using System.Text.Json.Serialization;

namespace Hive.Contracts.Inbox;

/// <summary>
/// A lightweight realtime invalidation signal. The REST inbox snapshot remains authoritative.
/// </summary>
public sealed record InboxChangedNotification
{
    public InboxChangedNotification(
        long sequence,
        string organizationId,
        string itemId,
        string assignedPositionId,
        InboxChangeType changeType,
        DateTimeOffset changedAtUtc)
    {
        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequence),
                sequence,
                "Inbox notification sequence must be positive.");
        }

        Sequence = sequence;
        OrganizationId = InboxContractGuards.Identifier(
            organizationId,
            nameof(organizationId));
        ItemId = InboxContractGuards.ItemIdentifier(itemId, nameof(itemId));
        AssignedPositionId = InboxContractGuards.Identifier(
            assignedPositionId,
            nameof(assignedPositionId));
        ChangeType = InboxContractGuards.DefinedEnum(changeType, nameof(changeType));
        ChangedAtUtc = InboxContractGuards.UtcTimestamp(
            changedAtUtc,
            nameof(changedAtUtc));
    }

    [JsonPropertyName("sequence")]
    public long Sequence { get; }

    [JsonPropertyName("organization_id")]
    public string OrganizationId { get; }

    [JsonPropertyName("item_id")]
    public string ItemId { get; }

    [JsonPropertyName("assigned_position_id")]
    public string AssignedPositionId { get; }

    [JsonPropertyName("change_type")]
    public InboxChangeType ChangeType { get; }

    [JsonPropertyName("changed_at_utc")]
    public DateTimeOffset ChangedAtUtc { get; }
}
