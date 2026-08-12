using Hive.Domain.OccupantChannels;
using Hive.Infrastructure.Identity;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Hive.Infrastructure.OccupantChannels;

internal sealed class SmtpOccupantChannel(
    IOccupantEmailBindingResolver bindingResolver,
    ISmtpOccupantTransport transport,
    SmtpOccupantEmailRenderer renderer,
    ISmtpRetryDelay retryDelay,
    IOptions<SmtpOccupantChannelOptions> options) : IOccupantChannel
{
    private readonly SmtpOccupantChannelOptions _options = options.Value;

    public async Task<OccupantChannelDeliveryResult> DeliverAsync(
        OccupantChannelDeliveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_options.Enabled)
        {
            return Failed(OccupantChannelDeliveryErrorCode.ConfigurationInvalid, retryable: false);
        }

        var query = new OccupantEmailBindingQuery(
            request.OrganizationId,
            request.PositionId,
            request.OccupantId,
            request.UserId,
            request.OccupantChannelBindingId);
        var backoff = _options.InitialBackoff;
        var lastFailure = Failed(OccupantChannelDeliveryErrorCode.Unknown, retryable: true);

        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Canceled();
            }

            var resolution = await ResolveAsync(query, cancellationToken).ConfigureAwait(false);
            if (resolution.Result is { } resolutionFailure)
            {
                lastFailure = resolutionFailure;
            }
            else
            {
                var endpoint = resolution.Endpoint!;
                if (!MailboxAddress.TryParse(endpoint, out var recipient) ||
                    !string.Equals(endpoint, recipient.Address, StringComparison.OrdinalIgnoreCase))
                {
                    return Failed(
                        OccupantChannelDeliveryErrorCode.ConfigurationInvalid,
                        retryable: false);
                }

                var message = renderer.Render(request, recipient.Address, _options);
                lastFailure = await SendAttemptAsync(message, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (lastFailure.IsSuccess || !lastFailure.Error!.IsRetryable ||
                attempt == _options.MaxAttempts)
            {
                return lastFailure;
            }

            try
            {
                await retryDelay.DelayAsync(backoff, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Canceled();
            }

            backoff = NextBackoff(backoff, _options.MaxBackoff);
        }

        return lastFailure;
    }

    private async Task<(string? Endpoint, OccupantChannelDeliveryResult? Result)> ResolveAsync(
        OccupantEmailBindingQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolution = await bindingResolver
                .ResolveActiveAsync(query, cancellationToken)
                .ConfigureAwait(false);
            return resolution.Status switch
            {
                OccupantEmailBindingResolutionStatus.Active
                    when resolution.NormalizedEndpoint is not null =>
                    (resolution.NormalizedEndpoint, null),
                OccupantEmailBindingResolutionStatus.Missing =>
                    (null, Failed(OccupantChannelDeliveryErrorCode.BindingUnavailable, false)),
                OccupantEmailBindingResolutionStatus.Revoked =>
                    (null, Failed(OccupantChannelDeliveryErrorCode.BindingRevoked, false)),
                OccupantEmailBindingResolutionStatus.IdentityUnavailable =>
                    (null, Failed(OccupantChannelDeliveryErrorCode.ChannelUnavailable, true)),
                _ => (null, Failed(OccupantChannelDeliveryErrorCode.ConfigurationInvalid, false)),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (null, Canceled());
        }
        catch (Exception)
        {
            return (null, Failed(OccupantChannelDeliveryErrorCode.ChannelUnavailable, true));
        }
    }

    private async Task<OccupantChannelDeliveryResult> SendAttemptAsync(
        SmtpOutboundMessage message,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_options.AttemptTimeout);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        Task<OccupantChannelDeliveryResult>? sendTask = null;

        try
        {
            sendTask = transport.SendAsync(message, attempt.Token);
            return await sendTask.WaitAsync(attempt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObserveLateCompletion(sendTask);
            return Canceled();
        }
        catch (OperationCanceledException)
        {
            ObserveLateCompletion(sendTask);
            return Failed(OccupantChannelDeliveryErrorCode.Timeout, retryable: true);
        }
        catch (Exception)
        {
            return Failed(OccupantChannelDeliveryErrorCode.Unknown, retryable: true);
        }
    }

    private static void ObserveLateCompletion(Task? task)
    {
        if (task is null)
        {
            return;
        }

        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static TimeSpan NextBackoff(TimeSpan current, TimeSpan maximum)
    {
        if (current >= maximum || current.Ticks > maximum.Ticks / 2)
        {
            return maximum;
        }

        return TimeSpan.FromTicks(Math.Min(current.Ticks * 2, maximum.Ticks));
    }

    private static OccupantChannelDeliveryResult Canceled() =>
        Failed(OccupantChannelDeliveryErrorCode.Canceled, retryable: true);

    private static OccupantChannelDeliveryResult Failed(
        OccupantChannelDeliveryErrorCode code,
        bool retryable) =>
        OccupantChannelDeliveryResult.Failed(new OccupantChannelDeliveryError(code, retryable));
}
