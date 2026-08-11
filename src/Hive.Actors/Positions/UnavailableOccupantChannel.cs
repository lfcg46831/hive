using Hive.Domain.OccupantChannels;

namespace Hive.Actors.Positions;

internal sealed class UnavailableOccupantChannel : IOccupantChannel
{
    public static UnavailableOccupantChannel Instance { get; } = new();

    private UnavailableOccupantChannel()
    {
    }

    public Task<OccupantChannelDeliveryResult> DeliverAsync(
        OccupantChannelDeliveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(OccupantChannelDeliveryResult.Failed(
            new OccupantChannelDeliveryError(
                OccupantChannelDeliveryErrorCode.ChannelUnavailable,
                isRetryable: true)));
    }
}

internal sealed class UnavailableOccupantChannelDeliveryRequestFactory
    : IOccupantChannelDeliveryRequestFactory
{
    public static UnavailableOccupantChannelDeliveryRequestFactory Instance { get; } = new();

    private UnavailableOccupantChannelDeliveryRequestFactory()
    {
    }

    public OccupantChannelDeliveryRequest Create(OccupantChannelDeliveryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        throw new InvalidOperationException(
            "No occupant-channel delivery request factory has been configured.");
    }
}
