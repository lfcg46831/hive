namespace Hive.Domain.Ai;

/// <summary>
/// Provider-neutral concurrency and sliding-window admission limits for one provider.
/// This value describes policy only; it does not own counters or time-window state.
/// </summary>
public sealed record AiProviderRateLimitPolicy
{
    public const int DefaultMaxConcurrentCalls = 4;
    public const int DefaultMaxCallsPerWindow = 60;

    public static TimeSpan DefaultWindow { get; } = TimeSpan.FromMinutes(1);

    public static AiProviderRateLimitPolicy Default { get; } = new();

    public AiProviderRateLimitPolicy()
        : this(
            DefaultMaxConcurrentCalls,
            DefaultMaxCallsPerWindow,
            DefaultWindow)
    {
    }

    public AiProviderRateLimitPolicy(
        int maxConcurrentCalls,
        int maxCallsPerWindow,
        TimeSpan window)
    {
        RequirePositive(maxConcurrentCalls, nameof(maxConcurrentCalls));
        RequirePositive(maxCallsPerWindow, nameof(maxCallsPerWindow));
        RequirePositive(window, nameof(window));

        MaxConcurrentCalls = maxConcurrentCalls;
        MaxCallsPerWindow = maxCallsPerWindow;
        Window = window;
    }

    public int MaxConcurrentCalls { get; }

    public int MaxCallsPerWindow { get; }

    public TimeSpan Window { get; }

    private static void RequirePositive(int value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Provider rate limits must be greater than zero.");
        }
    }

    private static void RequirePositive(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Provider rate-limit window must be greater than zero.");
        }
    }
}

/// <summary>
/// Provider-neutral bounded queue policy. A queue is disabled only when both values are zero.
/// </summary>
public sealed record AiProviderQueuePolicy
{
    public const int DefaultMaxDepth = 100;

    public static TimeSpan DefaultMaxWait { get; } = TimeSpan.FromSeconds(30);

    public static AiProviderQueuePolicy Default { get; } = new();

    public AiProviderQueuePolicy()
        : this(DefaultMaxDepth, DefaultMaxWait)
    {
    }

    public AiProviderQueuePolicy(int maxDepth, TimeSpan maxWait)
    {
        if (maxDepth < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxDepth),
                maxDepth,
                "Provider queue depth cannot be negative.");
        }

        if (maxWait < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxWait),
                maxWait,
                "Provider queue wait cannot be negative.");
        }

        if ((maxDepth == 0) != (maxWait == TimeSpan.Zero))
        {
            throw new ArgumentException(
                "Provider queue depth and wait must either both be zero or both be positive.",
                nameof(maxWait));
        }

        MaxDepth = maxDepth;
        MaxWait = maxWait;
    }

    public int MaxDepth { get; }

    public TimeSpan MaxWait { get; }

    public bool IsEnabled => MaxDepth > 0;
}

/// <summary>
/// Provider-neutral retry parameters. Max attempts includes the initial provider call.
/// </summary>
public sealed record AiProviderRetryPolicy
{
    public const int DefaultMaxAttempts = 3;
    public const decimal DefaultJitterRatio = 0.20m;

    public static TimeSpan DefaultInitialBackoff { get; } = TimeSpan.FromMilliseconds(250);

    public static TimeSpan DefaultMaxBackoff { get; } = TimeSpan.FromSeconds(5);

    public static AiProviderRetryPolicy Default { get; } = new();

    public AiProviderRetryPolicy()
        : this(
            DefaultMaxAttempts,
            DefaultInitialBackoff,
            DefaultMaxBackoff,
            DefaultJitterRatio)
    {
    }

    public AiProviderRetryPolicy(
        int maxAttempts,
        TimeSpan initialBackoff,
        TimeSpan maxBackoff,
        decimal jitterRatio)
    {
        if (maxAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxAttempts),
                maxAttempts,
                "Provider retry attempts must be greater than zero.");
        }

        if (initialBackoff <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialBackoff),
                initialBackoff,
                "Provider retry initial backoff must be greater than zero.");
        }

        if (maxBackoff <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxBackoff),
                maxBackoff,
                "Provider retry maximum backoff must be greater than zero.");
        }

        if (maxBackoff < initialBackoff)
        {
            throw new ArgumentException(
                "Provider retry maximum backoff cannot be less than the initial backoff.",
                nameof(maxBackoff));
        }

        if (jitterRatio is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(jitterRatio),
                jitterRatio,
                "Provider retry jitter ratio must be between zero and one.");
        }

        MaxAttempts = maxAttempts;
        InitialBackoff = initialBackoff;
        MaxBackoff = maxBackoff;
        JitterRatio = jitterRatio;
    }

    public int MaxAttempts { get; }

    public TimeSpan InitialBackoff { get; }

    public TimeSpan MaxBackoff { get; }

    public decimal JitterRatio { get; }
}

/// <summary>
/// Provider-neutral sliding-window circuit-breaker thresholds. This value carries no circuit state.
/// </summary>
public sealed record AiProviderCircuitBreakerPolicy
{
    public const int DefaultFailureThreshold = 5;
    public const int DefaultHalfOpenMaxConcurrentProbes = 1;

    public static TimeSpan DefaultSamplingWindow { get; } = TimeSpan.FromSeconds(60);

    public static TimeSpan DefaultOpenDuration { get; } = TimeSpan.FromSeconds(30);

    public static AiProviderCircuitBreakerPolicy Default { get; } = new();

    public AiProviderCircuitBreakerPolicy()
        : this(
            DefaultSamplingWindow,
            DefaultFailureThreshold,
            DefaultOpenDuration,
            DefaultHalfOpenMaxConcurrentProbes)
    {
    }

    public AiProviderCircuitBreakerPolicy(
        TimeSpan samplingWindow,
        int failureThreshold,
        TimeSpan openDuration,
        int halfOpenMaxConcurrentProbes)
    {
        RequirePositive(samplingWindow, nameof(samplingWindow));
        RequirePositive(failureThreshold, nameof(failureThreshold));
        RequirePositive(openDuration, nameof(openDuration));
        RequirePositive(halfOpenMaxConcurrentProbes, nameof(halfOpenMaxConcurrentProbes));

        SamplingWindow = samplingWindow;
        FailureThreshold = failureThreshold;
        OpenDuration = openDuration;
        HalfOpenMaxConcurrentProbes = halfOpenMaxConcurrentProbes;
    }

    public TimeSpan SamplingWindow { get; }

    public int FailureThreshold { get; }

    public TimeSpan OpenDuration { get; }

    public int HalfOpenMaxConcurrentProbes { get; }

    private static void RequirePositive(int value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Provider circuit-breaker limits must be greater than zero.");
        }
    }

    private static void RequirePositive(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Provider circuit-breaker durations must be greater than zero.");
        }
    }
}

/// <summary>
/// Complete immutable resilience policy applied to one provider by the gateway runtime.
/// Provider selection, host configuration binding, and all mutable runtime state remain outside
/// this domain contract.
/// </summary>
public sealed record AiProviderResiliencePolicy
{
    public static AiProviderResiliencePolicy Default { get; } = new();

    public AiProviderResiliencePolicy()
        : this(
            AiProviderRateLimitPolicy.Default,
            AiProviderQueuePolicy.Default,
            AiProviderRetryPolicy.Default,
            AiProviderCircuitBreakerPolicy.Default)
    {
    }

    public AiProviderResiliencePolicy(
        AiProviderRateLimitPolicy rateLimit,
        AiProviderQueuePolicy queue,
        AiProviderRetryPolicy retry,
        AiProviderCircuitBreakerPolicy circuitBreaker)
    {
        ArgumentNullException.ThrowIfNull(rateLimit);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(retry);
        ArgumentNullException.ThrowIfNull(circuitBreaker);

        RateLimit = rateLimit;
        Queue = queue;
        Retry = retry;
        CircuitBreaker = circuitBreaker;
    }

    public AiProviderRateLimitPolicy RateLimit { get; }

    public AiProviderQueuePolicy Queue { get; }

    public AiProviderRetryPolicy Retry { get; }

    public AiProviderCircuitBreakerPolicy CircuitBreaker { get; }
}
