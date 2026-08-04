using System.Text.Json.Serialization;

namespace Hive.Contracts.Inbox;

[JsonConverter(typeof(JsonStringEnumConverter<InboxResponseState>))]
public enum InboxResponseState
{
    NotApplicable,
    AwaitingResponse,
    InProgress,
    Responded,
}
