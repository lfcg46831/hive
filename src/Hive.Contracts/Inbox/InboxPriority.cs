using System.Text.Json.Serialization;

namespace Hive.Contracts.Inbox;

[JsonConverter(typeof(JsonStringEnumConverter<InboxPriority>))]
public enum InboxPriority
{
    Low,
    Normal,
    High,
    Critical,
}
