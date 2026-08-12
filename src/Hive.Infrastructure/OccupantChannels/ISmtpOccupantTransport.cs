using Hive.Domain.OccupantChannels;

namespace Hive.Infrastructure.OccupantChannels;

internal interface ISmtpOccupantTransport
{
    Task<OccupantChannelDeliveryResult> SendAsync(
        SmtpOutboundMessage message,
        CancellationToken cancellationToken);
}

internal interface ISmtpRetryDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemSmtpRetryDelay : ISmtpRetryDelay
{
    public static SystemSmtpRetryDelay Instance { get; } = new();

    private SystemSmtpRetryDelay()
    {
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}
