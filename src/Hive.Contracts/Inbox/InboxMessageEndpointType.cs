using System.Text.Json.Serialization;

namespace Hive.Contracts.Inbox;

[JsonConverter(typeof(JsonStringEnumConverter<InboxMessageEndpointType>))]
public enum InboxMessageEndpointType
{
    Position,
    OrganizationOwner,
}
