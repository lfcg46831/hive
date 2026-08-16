using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Infrastructure.Ai;

namespace Hive.Tests;

public sealed class AiProviderCircuitBreakerTests
{
    private static readonly OrganizationId Organization =
        OrganizationId.From("acme-delivery");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public void State_and_reason_contracts_are_closed_and_canonical()
    {
        Assert.Equal(
            "closed",
            AiProviderCircuitStateContract.ToWireValue(AiProviderCircuitState.Closed));
        Assert.Equal(
            AiProviderCircuitState.HalfOpen,
            AiProviderCircuitStateContract.ParseWireValue("half-open"));
        Assert.Equal(
            "failure-threshold-reached",
            AiProviderCircuitTransitionReasonContract.ToWireValue(
                AiProviderCircuitTransitionReason.FailureThresholdReached));
        Assert.Equal(
            AiProviderCircuitTransitionReason.HalfOpenProbeSucceeded,
            AiProviderCircuitTransitionReasonContract.ParseWireValue(
                "half-open-probe-succeeded"));

        Assert.False(AiProviderCircuitStateContract.TryParseWireValue("HalfOpen", out _));
        Assert.False(
            AiProviderCircuitTransitionReasonContract.TryParseWireValue(
                "probe-succeeded",
                out _));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AiProviderCircuitStateContract.ToWireValue((AiProviderCircuitState)99));
        Assert.Throws<ArgumentException>(
            () => AiProviderCircuitTransitionReasonContract.ParseWireValue(
                "FailureThresholdReached"));
    }

    [Fact]
    public void Transition_contract_requires_matching_states_reason_and_causal_error()
    {
        var at = new DateTimeOffset(2026, 8, 15, 9, 0, 0, TimeSpan.Zero);
        var transition = new AiProviderCircuitTransition(
            Organization,
            Position,
            Thread,
            Message,
            "openai",
            AiProviderCircuitState.Closed,
            AiProviderCircuitState.Open,
            at,
            AiProviderCircuitTransitionReason.FailureThresholdReached,
            AiGatewayErrorCode.Timeout);

        Assert.Equal("openai", transition.ProviderId);
        Assert.Equal(at, transition.OccurredAt);
        Assert.Equal(AiGatewayErrorCode.Timeout, transition.ErrorCode);
        Assert.Throws<ArgumentException>(() => new AiProviderCircuitTransition(
            Organization,
            Position,
            Thread,
            Message,
            "openai",
            AiProviderCircuitState.Open,
            AiProviderCircuitState.Closed,
            at,
            AiProviderCircuitTransitionReason.OpenDurationElapsed));
        Assert.Throws<ArgumentException>(() => new AiProviderCircuitTransition(
            Organization,
            Position,
            Thread,
            Message,
            "openai",
            AiProviderCircuitState.Closed,
            AiProviderCircuitState.Open,
            at,
            AiProviderCircuitTransitionReason.FailureThresholdReached));
        Assert.Throws<ArgumentException>(() => new AiProviderCircuitTransition(
            Organization,
            Position,
            Thread,
            Message,
            providerId: null,
            AiProviderCircuitState.Open,
            AiProviderCircuitState.HalfOpen,
            at,
            AiProviderCircuitTransitionReason.OpenDurationElapsed,
            AiGatewayErrorCode.Timeout));
    }

    [Fact]
    public void Closed_window_expires_at_exact_boundary_and_threshold_opens()
    {
        var clock = new ManualTimeProvider();
        var publisher = new CapturingTransitionPublisher();
        var breaker = CreateBreaker(
            clock,
            publisher,
            Policy(
                failureThreshold: 2,
                samplingWindow: TimeSpan.FromSeconds(1)));
        var request = Request("openai", "model-a");

        Fail(breaker.Acquire(request), AiGatewayErrorCode.Timeout);
        clock.Advance(TimeSpan.FromSeconds(1));
        Fail(breaker.Acquire(request), AiGatewayErrorCode.ProviderUnavailable);

        Assert.Empty(publisher.Transitions);

        Fail(breaker.Acquire(request), AiGatewayErrorCode.QuotaExceeded);

        var opened = Assert.Single(publisher.Transitions);
        Assert.Equal(AiProviderCircuitState.Closed, opened.PreviousState);
        Assert.Equal(AiProviderCircuitState.Open, opened.CurrentState);
        Assert.Equal(
            AiProviderCircuitTransitionReason.FailureThresholdReached,
            opened.Reason);
        Assert.Equal(AiGatewayErrorCode.QuotaExceeded, opened.ErrorCode);
        Assert.Equal("openai", opened.ProviderId);
        Assert.Equal(clock.GetUtcNow(), opened.OccurredAt);
        Assert.Equal(Organization, opened.OrganizationId);
        Assert.Equal(Position, opened.PositionId);
        Assert.Equal(Thread, opened.ThreadId);
        Assert.Equal(Message, opened.MessageId);

        var rejected = breaker.Acquire(request);
        Assert.False(rejected.IsAllowed);
        Assert.Equal(AiGatewayErrorCode.ProviderUnavailable, rejected.Error!.Code);
        Assert.Equal(AiGatewayErrorReason.CircuitOpen, rejected.Error.Reason);
        Assert.Equal("AI provider circuit is open.", rejected.Error.Message);
        Assert.False(rejected.Error.IsRetryable);
    }

    [Fact]
    public void Closed_counts_only_provider_health_failures_and_success_does_not_clear_sample()
    {
        var clock = new ManualTimeProvider();
        var publisher = new CapturingTransitionPublisher();
        var breaker = CreateBreaker(
            clock,
            publisher,
            Policy(failureThreshold: 2));
        var request = Request("openai", "model-a");

        breaker.Acquire(request).Lease!.Observe(Failure(
            request,
            AiGatewayErrorCode.Timeout));
        breaker.Acquire(request).Lease!.Observe(Success(request));
        breaker.Acquire(request).Lease!.Observe(Failure(
            request,
            AiGatewayErrorCode.ProviderRejected));

        Assert.Empty(publisher.Transitions);

        breaker.Acquire(request).Lease!.Observe(Failure(
            request,
            AiGatewayErrorCode.QuotaExceeded));

        var opened = Assert.Single(publisher.Transitions);
        Assert.Equal(AiGatewayErrorCode.QuotaExceeded, opened.ErrorCode);
    }

    [Fact]
    public void Exact_open_boundary_admits_configured_probes_and_success_closes()
    {
        var clock = new ManualTimeProvider();
        var publisher = new CapturingTransitionPublisher();
        var breaker = CreateBreaker(
            clock,
            publisher,
            Policy(
                failureThreshold: 1,
                openDuration: TimeSpan.FromSeconds(5),
                halfOpenMaxConcurrentProbes: 2));
        var request = Request("openai", "model-a");

        Fail(breaker.Acquire(request), AiGatewayErrorCode.Timeout);
        clock.Advance(TimeSpan.FromSeconds(5));

        var firstProbe = breaker.Acquire(request);
        var secondProbe = breaker.Acquire(Request("openai", "model-b"));
        var excessProbe = breaker.Acquire(request);

        Assert.True(firstProbe.IsAllowed);
        Assert.True(secondProbe.IsAllowed);
        Assert.False(excessProbe.IsAllowed);
        Assert.Equal(AiGatewayErrorReason.CircuitOpen, excessProbe.Error!.Reason);
        Assert.Equal(
            AiProviderCircuitTransitionReason.OpenDurationElapsed,
            publisher.Transitions[1].Reason);

        firstProbe.Lease!.Observe(Success(request));
        secondProbe.Lease!.ObserveFailure(AiGatewayErrorCode.Timeout);

        Assert.Equal(3, publisher.Transitions.Count);
        Assert.Equal(
            AiProviderCircuitTransitionReason.HalfOpenProbeSucceeded,
            publisher.Transitions[2].Reason);
        Assert.Null(publisher.Transitions[2].ErrorCode);
        Assert.True(breaker.Acquire(request).IsAllowed);
    }

    [Fact]
    public void Failed_probe_reopens_and_restarts_the_full_open_duration()
    {
        var clock = new ManualTimeProvider();
        var publisher = new CapturingTransitionPublisher();
        var breaker = CreateBreaker(
            clock,
            publisher,
            Policy(
                failureThreshold: 1,
                openDuration: TimeSpan.FromSeconds(5)));
        var request = Request("openai", "model-a");

        Fail(breaker.Acquire(request), AiGatewayErrorCode.Timeout);
        clock.Advance(TimeSpan.FromSeconds(5));
        var probe = breaker.Acquire(request);
        Fail(probe, AiGatewayErrorCode.InvalidProviderResponse);

        Assert.Equal(
            AiProviderCircuitTransitionReason.HalfOpenProbeFailed,
            publisher.Transitions[2].Reason);
        Assert.Equal(
            AiGatewayErrorCode.InvalidProviderResponse,
            publisher.Transitions[2].ErrorCode);

        clock.Advance(TimeSpan.FromMilliseconds(4_999));
        Assert.False(breaker.Acquire(request).IsAllowed);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(breaker.Acquire(request).IsAllowed);
    }

    [Fact]
    public void Neutral_probe_results_and_disposal_release_the_probe_without_transition()
    {
        var clock = new ManualTimeProvider();
        var publisher = new CapturingTransitionPublisher();
        var breaker = CreateBreaker(
            clock,
            publisher,
            Policy(failureThreshold: 1));
        var request = Request("openai", "model-a");

        Fail(breaker.Acquire(request), AiGatewayErrorCode.Timeout);
        clock.Advance(TimeSpan.FromSeconds(5));

        var rejectedByProvider = breaker.Acquire(request);
        rejectedByProvider.Lease!.Observe(Failure(
            request,
            AiGatewayErrorCode.ProviderRejected));
        var locallyRejected = breaker.Acquire(request);
        locallyRejected.Lease!.Dispose();
        var replacement = breaker.Acquire(request);

        Assert.True(replacement.IsAllowed);
        Assert.Equal(2, publisher.Transitions.Count);
        Assert.Equal(
            AiProviderCircuitTransitionReason.OpenDurationElapsed,
            publisher.Transitions[1].Reason);
        replacement.Lease!.Dispose();
    }

    [Fact]
    public void Providers_are_isolated_and_late_results_from_old_generations_are_inert()
    {
        var clock = new ManualTimeProvider();
        var publisher = new CapturingTransitionPublisher();
        var breaker = CreateBreaker(
            clock,
            publisher,
            Policy(failureThreshold: 1, halfOpenMaxConcurrentProbes: 2));
        var openAiRequest = Request("openai", "model-a");
        var lateSuccess = breaker.Acquire(openAiRequest);
        var opener = breaker.Acquire(Request("openai", "model-b"));

        Fail(opener, AiGatewayErrorCode.Timeout);
        lateSuccess.Lease!.Observe(Success(openAiRequest));

        Assert.False(breaker.Acquire(openAiRequest).IsAllowed);
        var anthropic = breaker.Acquire(Request("anthropic", "model-a"));
        Assert.True(anthropic.IsAllowed);
        anthropic.Lease!.Dispose();

        clock.Advance(TimeSpan.FromSeconds(5));
        var successfulProbe = breaker.Acquire(openAiRequest);
        var lateFailedProbe = breaker.Acquire(Request("openai", "model-b"));
        successfulProbe.Lease!.Observe(Success(openAiRequest));
        lateFailedProbe.Lease!.ObserveFailure(AiGatewayErrorCode.Timeout);

        Assert.True(breaker.Acquire(openAiRequest).IsAllowed);
        Assert.DoesNotContain(
            publisher.Transitions,
            transition => transition.Reason ==
                AiProviderCircuitTransitionReason.HalfOpenProbeFailed);
    }

    [Fact]
    public void Requests_without_provider_share_the_legacy_bucket_and_audit_no_provider_id()
    {
        var clock = new ManualTimeProvider();
        var publisher = new CapturingTransitionPublisher();
        var breaker = CreateBreaker(
            clock,
            publisher,
            Policy(failureThreshold: 1));

        Fail(breaker.Acquire(LegacyRequest()), AiGatewayErrorCode.Timeout);

        Assert.False(breaker.Acquire(LegacyRequest()).IsAllowed);
        Assert.Null(Assert.Single(publisher.Transitions).ProviderId);
    }

    [Fact]
    public async Task Gateway_checks_circuit_before_admission_on_every_retry()
    {
        var clock = new ManualTimeProvider();
        var publisher = new CapturingTransitionPublisher();
        var policy = Policy(
            failureThreshold: 2,
            retryAttempts: 3);
        var resolver = new FixedPolicyResolver(policy);
        var breaker = new AiProviderCircuitBreaker(resolver, clock, publisher);
        using var innerLimiter = new AiProviderAdmissionLimiter(resolver, clock);
        var limiter = new CountingAdmissionLimiter(innerLimiter);
        var provider = new TimeoutProvider();
        var gateway = new AiGateway(
            provider,
            timeProvider: clock,
            admissionLimiter: limiter,
            resiliencePolicyResolver: resolver,
            retryBackoff: new ImmediateRetryBackoff(),
            circuitBreaker: breaker);

        var response = await gateway.CompleteAsync(Request("openai", "model-a"));

        Assert.True(response.IsFailure);
        Assert.Equal(AiGatewayErrorReason.CircuitOpen, response.Error!.Reason);
        Assert.Equal(2, provider.CallCount);
        Assert.Equal(2, limiter.AcquireCount);
        Assert.Equal(
            AiProviderCircuitTransitionReason.FailureThresholdReached,
            Assert.Single(publisher.Transitions).Reason);
    }

    [Fact]
    public async Task Provider_exception_counts_as_unknown_but_caller_cancellation_does_not()
    {
        var clock = new ManualTimeProvider();
        var policy = Policy(failureThreshold: 1, retryAttempts: 1);
        var resolver = new FixedPolicyResolver(policy);
        var exceptionPublisher = new CapturingTransitionPublisher();
        var exceptionCircuit = new AiProviderCircuitBreaker(
            resolver,
            clock,
            exceptionPublisher);
        using var exceptionLimiter = new AiProviderAdmissionLimiter(resolver, clock);
        var throwingProvider = new ThrowingProvider();
        var throwingGateway = new AiGateway(
            throwingProvider,
            timeProvider: clock,
            admissionLimiter: exceptionLimiter,
            resiliencePolicyResolver: resolver,
            retryBackoff: new ImmediateRetryBackoff(),
            circuitBreaker: exceptionCircuit);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => throwingGateway.CompleteAsync(Request("openai", "model-a")));

        var exceptionTransition = Assert.Single(exceptionPublisher.Transitions);
        Assert.Equal(AiGatewayErrorCode.Unknown, exceptionTransition.ErrorCode);
        var shortCircuited = await throwingGateway.CompleteAsync(
            Request("openai", "model-a"));
        Assert.Equal(AiGatewayErrorReason.CircuitOpen, shortCircuited.Error!.Reason);
        Assert.Equal(1, throwingProvider.CallCount);

        var cancellationPublisher = new CapturingTransitionPublisher();
        var cancellationCircuit = new AiProviderCircuitBreaker(
            resolver,
            clock,
            cancellationPublisher);
        using var cancellationLimiter = new AiProviderAdmissionLimiter(resolver, clock);
        var cancelableProvider = new CancelableProvider();
        var cancelableGateway = new AiGateway(
            cancelableProvider,
            timeProvider: clock,
            admissionLimiter: cancellationLimiter,
            resiliencePolicyResolver: resolver,
            retryBackoff: new ImmediateRetryBackoff(),
            circuitBreaker: cancellationCircuit);
        using var cancellation = new CancellationTokenSource();

        var canceledCall = cancelableGateway.CompleteAsync(
            Request("anthropic", "model-a"),
            cancellation.Token);
        await cancelableProvider.Started;
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledCall);
        Assert.Empty(cancellationPublisher.Transitions);
        var afterCancellation = cancellationCircuit.Acquire(
            Request("anthropic", "model-a"));
        Assert.True(afterCancellation.IsAllowed);
        afterCancellation.Lease!.Dispose();
    }

    private static AiProviderCircuitBreaker CreateBreaker(
        TimeProvider clock,
        IAiProviderCircuitTransitionPublisher publisher,
        AiProviderResiliencePolicy policy) =>
        new(new FixedPolicyResolver(policy), clock, publisher);

    private static AiProviderResiliencePolicy Policy(
        int failureThreshold,
        TimeSpan? samplingWindow = null,
        TimeSpan? openDuration = null,
        int halfOpenMaxConcurrentProbes = 1,
        int retryAttempts = 1) =>
        new(
            new AiProviderRateLimitPolicy(
                maxConcurrentCalls: 8,
                maxCallsPerWindow: 100,
                TimeSpan.FromMinutes(1)),
            new AiProviderQueuePolicy(
                maxDepth: 8,
                maxWait: TimeSpan.FromSeconds(1)),
            new AiProviderRetryPolicy(
                retryAttempts,
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(1),
                jitterRatio: 0m),
            new AiProviderCircuitBreakerPolicy(
                samplingWindow ?? TimeSpan.FromSeconds(10),
                failureThreshold,
                openDuration ?? TimeSpan.FromSeconds(5),
                halfOpenMaxConcurrentProbes));

    private static AiGatewayRequest Request(string providerId, string modelId) =>
        new(
            Organization,
            Position,
            Thread,
            Message,
            "Classify this bug.",
            provider: new AiProviderMetadata(providerId, modelId));

    private static AiGatewayRequest LegacyRequest() =>
        new(
            Organization,
            Position,
            Thread,
            Message,
            "Classify this bug.");

    private static AiGatewayResponse Success(AiGatewayRequest request) =>
        AiGatewayResponse.Succeeded(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            "Done.",
            AiFinishReason.Stop,
            request.Provider);

    private static AiGatewayResponse Failure(
        AiGatewayRequest request,
        AiGatewayErrorCode code) =>
        AiGatewayResponse.Failed(new AiGatewayError(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            code,
            "Provider failed.",
            isRetryable: code is AiGatewayErrorCode.Timeout or
                AiGatewayErrorCode.ProviderUnavailable,
            request.Provider));

    private static void Fail(
        AiProviderCircuitAdmission admission,
        AiGatewayErrorCode errorCode)
    {
        Assert.True(admission.IsAllowed);
        admission.Lease!.ObserveFailure(errorCode);
    }

    private sealed class FixedPolicyResolver(AiProviderResiliencePolicy policy)
        : IAiProviderResiliencePolicyResolver
    {
        public AiProviderResiliencePolicy Resolve(AiProviderMetadata? provider) => policy;
    }

    private sealed class CapturingTransitionPublisher
        : IAiProviderCircuitTransitionPublisher
    {
        public List<AiProviderCircuitTransition> Transitions { get; } = [];

        public void Publish(AiProviderCircuitTransition transition) =>
            Transitions.Add(transition);
    }

    private sealed class CountingAdmissionLimiter(IAiProviderAdmissionLimiter inner)
        : IAiProviderAdmissionLimiter
    {
        private int _acquireCount;

        public int AcquireCount => Volatile.Read(ref _acquireCount);

        public ValueTask<AiProviderAdmissionResult> AcquireAsync(
            AiGatewayRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _acquireCount);
            return inner.AcquireAsync(request, cancellationToken);
        }
    }

    private sealed class ImmediateRetryBackoff : IAiProviderRetryBackoff
    {
        public Task DelayAsync(
            AiProviderRetryPolicy policy,
            int failedAttemptNumber,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class TimeoutProvider : IAiGatewayProvider
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<AiGatewayResponse> CompleteAsync(
            AiGatewayRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(Failure(request, AiGatewayErrorCode.Timeout));
        }
    }

    private sealed class ThrowingProvider : IAiGatewayProvider
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<AiGatewayResponse> CompleteAsync(
            AiGatewayRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            throw new InvalidOperationException("Provider failed outside its adapter contract.");
        }
    }

    private sealed class CancelableProvider : IAiGatewayProvider
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public async Task<AiGatewayResponse> CompleteAsync(
            AiGatewayRequest request,
            CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Success(request);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _sync = new();
        private DateTimeOffset _utcNow =
            new(2026, 8, 15, 9, 0, 0, TimeSpan.Zero);
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync)
            {
                return _utcNow;
            }
        }

        public override long GetTimestamp()
        {
            lock (_sync)
            {
                return _timestamp;
            }
        }

        public void Advance(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(duration));
            }

            lock (_sync)
            {
                _utcNow += duration;
                _timestamp = checked(_timestamp + duration.Ticks);
            }
        }
    }
}
