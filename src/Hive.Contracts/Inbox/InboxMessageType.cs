using System.Text.Json.Serialization;

namespace Hive.Contracts.Inbox;

[JsonConverter(typeof(JsonStringEnumConverter<InboxMessageType>))]
public enum InboxMessageType
{
    Directive,
    Report,
    Escalation,
    Memo,
    PeerRequest,
    PeerResponse,
    ApprovalRequest,
    ApprovalDecision,
}
