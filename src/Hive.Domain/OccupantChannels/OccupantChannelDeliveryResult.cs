namespace Hive.Domain.OccupantChannels;

/// <summary>Mutually exclusive outcome of one occupant-channel delivery attempt.</summary>
public sealed record OccupantChannelDeliveryResult
{
    private OccupantChannelDeliveryResult(OccupantChannelDeliveryError? error)
    {
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public bool IsFailure => Error is not null;

    public OccupantChannelDeliveryError? Error { get; }

    public static OccupantChannelDeliveryResult Succeeded() => new(error: null);

    public static OccupantChannelDeliveryResult Failed(OccupantChannelDeliveryError error) =>
        new(error ?? throw new ArgumentNullException(nameof(error)));
}
