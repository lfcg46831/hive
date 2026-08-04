using System.Text.Json.Serialization;

namespace Hive.Contracts.Inbox;

/// <summary>
/// Public endpoint reference for the organizational messages supported by the human inbox.
/// </summary>
public sealed record InboxMessageEndpoint
{
    public InboxMessageEndpoint(
        InboxMessageEndpointType type,
        string? positionId = null)
    {
        Type = InboxContractGuards.DefinedEnum(type, nameof(type));
        PositionId = type switch
        {
            InboxMessageEndpointType.Position =>
                InboxContractGuards.Identifier(positionId!, nameof(positionId)),
            InboxMessageEndpointType.OrganizationOwner when positionId is null => null,
            InboxMessageEndpointType.OrganizationOwner => throw new ArgumentException(
                "An organization owner endpoint cannot specify a position identifier.",
                nameof(positionId)),
            _ => throw new InvalidOperationException("Validated endpoint type is not mapped."),
        };
    }

    [JsonPropertyName("type")]
    public InboxMessageEndpointType Type { get; }

    [JsonPropertyName("position_id")]
    public string? PositionId { get; }
}
