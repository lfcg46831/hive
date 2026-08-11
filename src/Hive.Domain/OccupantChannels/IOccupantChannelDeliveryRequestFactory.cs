namespace Hive.Domain.OccupantChannels;

/// <summary>
/// Builds the rendered, tokenized channel request from a durable organizational message. Rendering
/// and signed-token generation remain behind this seam so the proxy is transport-neutral.
/// </summary>
public interface IOccupantChannelDeliveryRequestFactory
{
    OccupantChannelDeliveryRequest Create(OccupantChannelDeliveryContext context);
}
