using System.Text.Json.Serialization;

namespace Hive.Contracts.Inbox;

[JsonConverter(typeof(JsonStringEnumConverter<InboxApprovalState>))]
public enum InboxApprovalState
{
    Pending,
    Approved,
    Rejected,
    Expired,
}
