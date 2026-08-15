using Hive.Domain.Ai;

namespace Hive.Infrastructure.Ai;

/// <summary>
/// Delays one retry after a failed provider attempt. The failed attempt number is
/// one-based and identifies the attempt that completed before this delay.
/// </summary>
public interface IAiProviderRetryBackoff
{
    Task DelayAsync(
        AiProviderRetryPolicy policy,
        int failedAttemptNumber,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Produces a provider-neutral symmetric jitter sample in the closed interval
/// [-1, 1].
/// </summary>
public interface IAiProviderRetryJitterSource
{
    decimal NextSymmetricSample();
}

/// <summary>
/// Applies capped exponential backoff with symmetric jitter using injected time
/// and randomness seams.
/// </summary>
public sealed class AiProviderRetryBackoff : IAiProviderRetryBackoff
{
    private readonly TimeProvider _timeProvider;
    private readonly IAiProviderRetryJitterSource _jitterSource;

    public AiProviderRetryBackoff(
        TimeProvider timeProvider,
        IAiProviderRetryJitterSource jitterSource)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _jitterSource = jitterSource ?? throw new ArgumentNullException(nameof(jitterSource));
    }

    public Task DelayAsync(
        AiProviderRetryPolicy policy,
        int failedAttemptNumber,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var delay = CalculateDelay(policy, failedAttemptNumber);
        return Task.Delay(delay, _timeProvider, cancellationToken);
    }

    internal TimeSpan CalculateDelay(
        AiProviderRetryPolicy policy,
        int failedAttemptNumber)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (failedAttemptNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failedAttemptNumber),
                failedAttemptNumber,
                "The failed provider attempt number must be greater than zero.");
        }

        var baseTicks = CappedExponentialTicks(policy, failedAttemptNumber);
        if (policy.JitterRatio == 0)
        {
            return TimeSpan.FromTicks(baseTicks);
        }

        var sample = _jitterSource.NextSymmetricSample();
        if (sample is < -1 or > 1)
        {
            throw new InvalidOperationException(
                "The AI provider retry jitter source returned a sample outside [-1, 1].");
        }

        var factor = 1m + (policy.JitterRatio * sample);
        var jitteredTicks = decimal.Round(
            baseTicks * factor,
            decimals: 0,
            MidpointRounding.AwayFromZero);
        var finalTicks = Math.Clamp(
            jitteredTicks,
            decimal.Zero,
            policy.MaxBackoff.Ticks);

        return TimeSpan.FromTicks(decimal.ToInt64(finalTicks));
    }

    private static long CappedExponentialTicks(
        AiProviderRetryPolicy policy,
        int failedAttemptNumber)
    {
        var exponent = failedAttemptNumber - 1;
        var initialTicks = policy.InitialBackoff.Ticks;
        var maximumTicks = policy.MaxBackoff.Ticks;

        if (exponent >= 63)
        {
            return maximumTicks;
        }

        var multiplier = 1L << exponent;
        return initialTicks > maximumTicks / multiplier
            ? maximumTicks
            : initialTicks * multiplier;
    }
}

internal sealed class SystemAiProviderRetryJitterSource
    : IAiProviderRetryJitterSource
{
    public decimal NextSymmetricSample() =>
        ((decimal)Random.Shared.NextDouble() * 2m) - 1m;
}
