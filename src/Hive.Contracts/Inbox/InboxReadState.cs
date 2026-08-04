using System.Text.Json.Serialization;

namespace Hive.Contracts.Inbox;

[JsonConverter(typeof(JsonStringEnumConverter<InboxReadState>))]
public enum InboxReadState
{
    Unread,
    Read,
}
