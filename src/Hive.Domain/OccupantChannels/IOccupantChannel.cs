namespace Hive.Domain.OccupantChannels;

/// <summary>
/// Channel-neutral outbound seam for notifying an occupant bound to a position.
/// Implementations resolve the opaque binding and perform transport-specific delivery.
/// </summary>
public interface IOccupantChannel
{
    Task<OccupantChannelDeliveryResult> DeliverAsync(
        OccupantChannelDeliveryRequest request,
        CancellationToken cancellationToken = default);
}
