namespace Hive.Domain.OccupantChannels;

/// <summary>
/// Transport-neutral failure information. Retryability is explicit so callers never infer policy
/// from transport-specific exception text.
/// </summary>
public sealed record OccupantChannelDeliveryError
{
    public OccupantChannelDeliveryError(
        OccupantChannelDeliveryErrorCode code,
        bool isRetryable)
    {
        Code = OccupantChannelDeliveryErrorCodeContract.RequireDefined(code, nameof(code));
        IsRetryable = isRetryable;
    }

    public OccupantChannelDeliveryErrorCode Code { get; }

    public bool IsRetryable { get; }
}
