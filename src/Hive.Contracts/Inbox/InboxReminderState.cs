using System.Text.Json.Serialization;

namespace Hive.Contracts.Inbox;

/// <summary>Whether an existing deadline policy has emitted a reminder for the item.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InboxReminderState>))]
public enum InboxReminderState
{
    None,
    Sent,
}
