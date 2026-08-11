namespace Hive.Domain.OccupantChannels;

public enum OccupantChannelDeliveryErrorCode
{
    BindingUnavailable = 1,
    BindingRevoked = 2,
    ConfigurationInvalid = 3,
    AuthenticationFailed = 4,
    RateLimited = 5,
    Timeout = 6,
    Canceled = 7,
    ChannelUnavailable = 8,
    DeliveryRejected = 9,
    Unknown = 10,
}

public static class OccupantChannelDeliveryErrorCodeContract
{
    public static OccupantChannelDeliveryErrorCode RequireDefined(
        OccupantChannelDeliveryErrorCode value,
        string parameterName) => value switch
        {
            OccupantChannelDeliveryErrorCode.BindingUnavailable or
            OccupantChannelDeliveryErrorCode.BindingRevoked or
            OccupantChannelDeliveryErrorCode.ConfigurationInvalid or
            OccupantChannelDeliveryErrorCode.AuthenticationFailed or
            OccupantChannelDeliveryErrorCode.RateLimited or
            OccupantChannelDeliveryErrorCode.Timeout or
            OccupantChannelDeliveryErrorCode.Canceled or
            OccupantChannelDeliveryErrorCode.ChannelUnavailable or
            OccupantChannelDeliveryErrorCode.DeliveryRejected or
            OccupantChannelDeliveryErrorCode.Unknown => value,
            _ => throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Occupant-channel delivery error code is undefined."),
        };

    public static string ToWireValue(OccupantChannelDeliveryErrorCode value) =>
        RequireDefined(value, nameof(value)) switch
        {
            OccupantChannelDeliveryErrorCode.BindingUnavailable => "binding-unavailable",
            OccupantChannelDeliveryErrorCode.BindingRevoked => "binding-revoked",
            OccupantChannelDeliveryErrorCode.ConfigurationInvalid => "configuration-invalid",
            OccupantChannelDeliveryErrorCode.AuthenticationFailed => "authentication-failed",
            OccupantChannelDeliveryErrorCode.RateLimited => "rate-limited",
            OccupantChannelDeliveryErrorCode.Timeout => "timeout",
            OccupantChannelDeliveryErrorCode.Canceled => "canceled",
            OccupantChannelDeliveryErrorCode.ChannelUnavailable => "channel-unavailable",
            OccupantChannelDeliveryErrorCode.DeliveryRejected => "delivery-rejected",
            OccupantChannelDeliveryErrorCode.Unknown => "unknown",
            _ => throw new InvalidOperationException(
                "Validated occupant-channel delivery error code is not mapped."),
        };
}
