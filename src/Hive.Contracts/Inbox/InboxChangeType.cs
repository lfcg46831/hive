using System.Text.Json.Serialization;

namespace Hive.Contracts.Inbox;

/// <summary>
/// The kind of committed inbox change that invalidated a person's REST snapshot.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<InboxChangeType>))]
public enum InboxChangeType
{
    NewItem,
    ReadStateChanged,
    ResponseStateChanged,
    ApprovalPending,
    DecisionIssued,
    DeadlineApproaching,
}
