using Hive.Domain.Ai;

namespace Hive.Infrastructure.Ai;

/// <summary>Local admission and circuit boundary; never starts another gateway journey.</summary>
public sealed class LocalAiGatewayAttemptExecutor(
    IAiGatewayProvider provider,
    IAiProviderAdmissionLimiter admissionLimiter,
    IAiProviderCircuitBreaker circuitBreaker) : IAiGatewayAttemptExecutor
{
    public async Task<AiGatewayAttemptResult> ExecuteAsync(
        AiGatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var circuitAdmission = circuitBreaker.Acquire(request);
        ArgumentNullException.ThrowIfNull(circuitAdmission);
        if (!circuitAdmission.IsAllowed)
        {
            return new(AiGatewayResponse.Failed(circuitAdmission.Error!), null, false);
        }

        using var circuitLease = circuitAdmission.Lease!;
        var admission = await admissionLimiter.AcquireAsync(request, cancellationToken)
            .ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(admission);
        if (!admission.IsAdmitted)
        {
            return new(AiGatewayResponse.Failed(admission.Error!), null, false);
        }

        using var lease = admission.Lease!;
        try
        {
            var response = await provider.CompleteAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (response is null)
            {
                circuitLease.ObserveFailure(AiGatewayErrorCode.InvalidProviderResponse);
                throw new InvalidOperationException("AI provider returned no response.");
            }

            circuitLease.Observe(response);
            return new(response, lease.QueueDuration, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            circuitLease.ObserveFailure(AiGatewayErrorCode.Unknown);
            throw;
        }
    }
}
