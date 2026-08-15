using Hive.Domain.Ai;

namespace Hive.Tests;

public sealed class AiProviderResiliencePolicyTests
{
    [Fact]
    public void Default_policy_is_complete_and_explicit()
    {
        var policy = AiProviderResiliencePolicy.Default;

        Assert.Equal(4, policy.RateLimit.MaxConcurrentCalls);
        Assert.Equal(60, policy.RateLimit.MaxCallsPerWindow);
        Assert.Equal(TimeSpan.FromMinutes(1), policy.RateLimit.Window);

        Assert.True(policy.Queue.IsEnabled);
        Assert.Equal(100, policy.Queue.MaxDepth);
        Assert.Equal(TimeSpan.FromSeconds(30), policy.Queue.MaxWait);

        Assert.Equal(3, policy.Retry.MaxAttempts);
        Assert.Equal(TimeSpan.FromMilliseconds(250), policy.Retry.InitialBackoff);
        Assert.Equal(TimeSpan.FromSeconds(5), policy.Retry.MaxBackoff);
        Assert.Equal(0.20m, policy.Retry.JitterRatio);

        Assert.Equal(TimeSpan.FromSeconds(60), policy.CircuitBreaker.SamplingWindow);
        Assert.Equal(5, policy.CircuitBreaker.FailureThreshold);
        Assert.Equal(TimeSpan.FromSeconds(30), policy.CircuitBreaker.OpenDuration);
        Assert.Equal(1, policy.CircuitBreaker.HalfOpenMaxConcurrentProbes);
    }

    [Fact]
    public void Custom_policy_preserves_all_component_values()
    {
        var rateLimit = new AiProviderRateLimitPolicy(2, 15, TimeSpan.FromSeconds(10));
        var queue = new AiProviderQueuePolicy(8, TimeSpan.FromSeconds(3));
        var retry = new AiProviderRetryPolicy(
            5,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(2),
            0.5m);
        var circuitBreaker = new AiProviderCircuitBreakerPolicy(
            TimeSpan.FromSeconds(20),
            3,
            TimeSpan.FromSeconds(12),
            2);

        var policy = new AiProviderResiliencePolicy(
            rateLimit,
            queue,
            retry,
            circuitBreaker);

        Assert.Same(rateLimit, policy.RateLimit);
        Assert.Same(queue, policy.Queue);
        Assert.Same(retry, policy.Retry);
        Assert.Same(circuitBreaker, policy.CircuitBreaker);
    }

    [Fact]
    public void Aggregate_requires_every_component()
    {
        Assert.Throws<ArgumentNullException>(() => new AiProviderResiliencePolicy(
            null!,
            AiProviderQueuePolicy.Default,
            AiProviderRetryPolicy.Default,
            AiProviderCircuitBreakerPolicy.Default));
        Assert.Throws<ArgumentNullException>(() => new AiProviderResiliencePolicy(
            AiProviderRateLimitPolicy.Default,
            null!,
            AiProviderRetryPolicy.Default,
            AiProviderCircuitBreakerPolicy.Default));
        Assert.Throws<ArgumentNullException>(() => new AiProviderResiliencePolicy(
            AiProviderRateLimitPolicy.Default,
            AiProviderQueuePolicy.Default,
            null!,
            AiProviderCircuitBreakerPolicy.Default));
        Assert.Throws<ArgumentNullException>(() => new AiProviderResiliencePolicy(
            AiProviderRateLimitPolicy.Default,
            AiProviderQueuePolicy.Default,
            AiProviderRetryPolicy.Default,
            null!));
    }

    [Fact]
    public void Rate_limit_requires_positive_limits_and_window()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AiProviderRateLimitPolicy(0, 1, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AiProviderRateLimitPolicy(1, 0, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AiProviderRateLimitPolicy(1, 1, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AiProviderRateLimitPolicy(1, 1, TimeSpan.FromTicks(-1)));
    }

    [Fact]
    public void Queue_can_only_be_disabled_by_zero_depth_and_zero_wait()
    {
        var disabled = new AiProviderQueuePolicy(0, TimeSpan.Zero);

        Assert.False(disabled.IsEnabled);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AiProviderQueuePolicy(-1, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AiProviderQueuePolicy(1, TimeSpan.FromTicks(-1)));
        Assert.Throws<ArgumentException>(() =>
            new AiProviderQueuePolicy(0, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() =>
            new AiProviderQueuePolicy(1, TimeSpan.Zero));
    }

    [Fact]
    public void Retry_requires_coherent_attempts_backoff_and_jitter()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiProviderRetryPolicy(
            0,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1),
            0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiProviderRetryPolicy(
            1,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(1),
            0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiProviderRetryPolicy(
            1,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.Zero,
            0));
        Assert.Throws<ArgumentException>(() => new AiProviderRetryPolicy(
            1,
            TimeSpan.FromMilliseconds(2),
            TimeSpan.FromMilliseconds(1),
            0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiProviderRetryPolicy(
            1,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1),
            -0.01m));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiProviderRetryPolicy(
            1,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1),
            1.01m));

        Assert.Equal(
            0m,
            new AiProviderRetryPolicy(
                1,
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(1),
                0).JitterRatio);
        Assert.Equal(
            1m,
            new AiProviderRetryPolicy(
                1,
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(1),
                1).JitterRatio);
    }

    [Fact]
    public void Circuit_breaker_requires_positive_window_threshold_duration_and_probes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AiProviderCircuitBreakerPolicy(
                TimeSpan.Zero,
                1,
                TimeSpan.FromSeconds(1),
                1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AiProviderCircuitBreakerPolicy(
                TimeSpan.FromSeconds(1),
                0,
                TimeSpan.FromSeconds(1),
                1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AiProviderCircuitBreakerPolicy(
                TimeSpan.FromSeconds(1),
                1,
                TimeSpan.Zero,
                1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AiProviderCircuitBreakerPolicy(
                TimeSpan.FromSeconds(1),
                1,
                TimeSpan.FromSeconds(1),
                0));
    }

    [Fact]
    public void Contract_surface_is_provider_neutral_and_domain_owned()
    {
        Type[] contractTypes =
        [
            typeof(AiProviderRateLimitPolicy),
            typeof(AiProviderQueuePolicy),
            typeof(AiProviderRetryPolicy),
            typeof(AiProviderCircuitBreakerPolicy),
            typeof(AiProviderResiliencePolicy),
        ];

        Assert.All(contractTypes, type =>
        {
            Assert.Equal("Hive.Domain.Ai", type.Namespace);
            Assert.Same(typeof(AiProviderResiliencePolicy).Assembly, type.Assembly);
            Assert.DoesNotContain(
                type.GetProperties(),
                property => property.Name.Contains("ProviderId", StringComparison.Ordinal));
        });
    }
}
