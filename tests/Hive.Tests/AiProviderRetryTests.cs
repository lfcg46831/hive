using System.Threading.Channels;
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Infrastructure.Ai;

namespace Hive.Tests;

public sealed class AiProviderRetryTests
{
    private static readonly OrganizationId Organization =
        OrganizationId.From("acme-delivery");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly AiProviderMetadata Provider =
        new("openai", "gpt-5-mini");

    [Fact]
    public void Backoff_applies_exponential_progression_symmetric_jitter_and_cap()
    {
        var jitter = new SequenceJitterSource(-1m, 0m, 1m, 1m);
        var backoff = new AiProviderRetryBackoff(TimeProvider.System, jitter);
        var policy = new AiProviderRetryPolicy(
            maxAttempts: 5,
            initialBackoff: TimeSpan.FromMilliseconds(100),
            maxBackoff: TimeSpan.FromMilliseconds(500),
            jitterRatio: 0.20m);

        Assert.Equal(
            TimeSpan.FromMilliseconds(80),
            backoff.CalculateDelay(policy, failedAttemptNumber: 1));
        Assert.Equal(
            TimeSpan.FromMilliseconds(200),
            backoff.CalculateDelay(policy, failedAttemptNumber: 2));
        Assert.Equal(
            TimeSpan.FromMilliseconds(480),
            backoff.CalculateDelay(policy, failedAttemptNumber: 3));
        Assert.Equal(
            TimeSpan.FromMilliseconds(500),
            backoff.CalculateDelay(policy, failedAttemptNumber: 4));
        Assert.Equal(4, jitter.CallCount);
    }

    [Fact]
    public void Backoff_with_zero_jitter_does_not_consume_randomness_and_handles_large_attempts()
    {
        var jitter = new ThrowingJitterSource();
        var backoff = new AiProviderRetryBackoff(TimeProvider.System, jitter);
        var policy = new AiProviderRetryPolicy(
            maxAttempts: int.MaxValue,
            initialBackoff: TimeSpan.FromTicks(1),
            maxBackoff: TimeSpan.FromSeconds(5),
            jitterRatio: 0m);

        Assert.Equal(
            TimeSpan.FromSeconds(5),
            backoff.CalculateDelay(policy, failedAttemptNumber: int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => backoff.CalculateDelay(policy, failedAttemptNumber: 0));
    }

    [Fact]
    public async Task Backoff_uses_injected_time_and_propagates_cancellation()
    {
        var timeProvider = new RecordingTimeProvider();
        var backoff = new AiProviderRetryBackoff(
            timeProvider,
            new SequenceJitterSource(-1m));
        var policy = new AiProviderRetryPolicy(
            maxAttempts: 2,
            initialBackoff: TimeSpan.FromMilliseconds(100),
            maxBackoff: TimeSpan.FromSeconds(1),
            jitterRatio: 0.25m);
        using var cancellation = new CancellationTokenSource();

        var delay = backoff.DelayAsync(policy, 1, cancellation.Token);

        Assert.Equal(TimeSpan.FromMilliseconds(75), timeProvider.LastDueTime);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await delay);
        Assert.True(timeProvider.LastTimerDisposed);
    }

    [Fact]
    public async Task Gateway_retries_eligible_failures_with_one_effective_request_and_correlation()
    {
        var retryPolicy = RetryPolicy(maxAttempts: 4);
        var resiliencePolicy = ResiliencePolicy(retryPolicy);
        var resolver = new FixedPolicyResolver(resiliencePolicy);
        using var limiter = new AiProviderAdmissionLimiter(resolver);
        var backoff = new RecordingBackoff();
        var provider = new ScriptedProvider((call, request) => call switch
        {
            1 => Failure(request, AiGatewayErrorCode.Timeout, isRetryable: true),
            2 => Failure(
                request,
                AiGatewayErrorCode.ProviderUnavailable,
                isRetryable: true),
            _ => Success(request),
        });
        var gateway = Gateway(provider, limiter, resolver, backoff);
        var request = Request(
            omitProvider: true,
            policy: new AiGatewayPolicy([Provider], hasAvailableBudget: true));

        var response = await gateway.CompleteAsync(request);

        Assert.True(response.IsSuccess);
        Assert.Equal(3, provider.Requests.Count);
        Assert.Equal([1, 2], backoff.FailedAttempts);
        var effectiveRequest = provider.Requests[0];
        Assert.NotSame(request, effectiveRequest);
        Assert.All(provider.Requests, attempt => Assert.Same(effectiveRequest, attempt));
        Assert.All(provider.Requests, attempt =>
        {
            Assert.Equal(Organization, attempt.OrganizationId);
            Assert.Equal(Position, attempt.PositionId);
            Assert.Equal(Thread, attempt.ThreadId);
            Assert.Equal(Message, attempt.MessageId);
            Assert.Equal(Provider, attempt.Provider);
        });
        Assert.All(provider.CancellationTokens, token => Assert.False(token.IsCancellationRequested));
    }

    [Fact]
    public async Task Gateway_returns_last_retryable_error_after_maximum_total_attempts()
    {
        var retryPolicy = RetryPolicy(maxAttempts: 3);
        var resiliencePolicy = ResiliencePolicy(retryPolicy);
        var resolver = new FixedPolicyResolver(resiliencePolicy);
        using var limiter = new AiProviderAdmissionLimiter(resolver);
        var backoff = new RecordingBackoff();
        var provider = new ScriptedProvider((call, request) => Failure(
            request,
            AiGatewayErrorCode.Timeout,
            isRetryable: true,
            message: $"Provider timeout {call}."));
        var gateway = Gateway(provider, limiter, resolver, backoff);

        var response = await gateway.CompleteAsync(Request());

        Assert.True(response.IsFailure);
        Assert.Equal(3, provider.Requests.Count);
        Assert.Equal([1, 2], backoff.FailedAttempts);
        Assert.Equal("Provider timeout 3.", response.Error!.Message);
        Assert.Equal(Message, response.Error.MessageId);
        Assert.Same(provider.Responses[^1], response);
    }

    [Theory]
    [InlineData(AiGatewayErrorCode.Timeout, false)]
    [InlineData(AiGatewayErrorCode.ProviderUnavailable, false)]
    [InlineData(AiGatewayErrorCode.ProviderRejected, true)]
    [InlineData(AiGatewayErrorCode.InvalidProviderResponse, true)]
    [InlineData(AiGatewayErrorCode.QuotaExceeded, true)]
    public async Task Gateway_does_not_retry_outside_the_closed_eligible_catalog(
        AiGatewayErrorCode code,
        bool isRetryable)
    {
        var retryPolicy = RetryPolicy(maxAttempts: 3);
        var resiliencePolicy = ResiliencePolicy(retryPolicy);
        var resolver = new FixedPolicyResolver(resiliencePolicy);
        using var limiter = new AiProviderAdmissionLimiter(resolver);
        var backoff = new RecordingBackoff();
        var provider = new ScriptedProvider((_, request) =>
            Failure(request, code, isRetryable));
        var gateway = Gateway(provider, limiter, resolver, backoff);

        var response = await gateway.CompleteAsync(Request());

        Assert.True(response.IsFailure);
        Assert.Single(provider.Requests);
        Assert.Empty(backoff.FailedAttempts);
    }

    [Fact]
    public async Task Gateway_does_not_retry_an_error_with_terminal_reason()
    {
        var retryPolicy = RetryPolicy(maxAttempts: 3);
        var resiliencePolicy = ResiliencePolicy(retryPolicy);
        var resolver = new FixedPolicyResolver(resiliencePolicy);
        using var limiter = new AiProviderAdmissionLimiter(resolver);
        var backoff = new RecordingBackoff();
        var provider = new ScriptedProvider((_, request) =>
            AiGatewayResponse.Failed(new AiGatewayError(
                request.OrganizationId,
                request.PositionId,
                request.ThreadId,
                request.MessageId,
                AiGatewayErrorCode.ProviderUnavailable,
                "Fallback exhausted.",
                isRetryable: true,
                request.Provider,
                diagnostics: null,
                AiGatewayErrorReason.FallbackExhausted)));
        var gateway = Gateway(provider, limiter, resolver, backoff);

        var response = await gateway.CompleteAsync(Request());

        Assert.True(response.IsFailure);
        Assert.Single(provider.Requests);
        Assert.Empty(backoff.FailedAttempts);
    }

    [Fact]
    public async Task Gateway_retries_local_overload_and_reacquires_admission_before_provider()
    {
        var retryPolicy = RetryPolicy(maxAttempts: 2);
        var resiliencePolicy = ResiliencePolicy(
            retryPolicy,
            maxConcurrentCalls: 1,
            queueDepth: 0,
            maxWait: TimeSpan.Zero);
        var resolver = new FixedPolicyResolver(resiliencePolicy);
        using var limiter = new AiProviderAdmissionLimiter(resolver);
        var provider = new BlockingProvider();
        Task<AiGatewayResponse>? firstTask = null;
        ProviderCall? firstCall = null;
        var backoff = new CallbackBackoff(async () =>
        {
            firstCall!.Succeed();
            await firstTask!;
        });
        var gateway = Gateway(provider, limiter, resolver, backoff);

        firstTask = gateway.CompleteAsync(Request());
        firstCall = await provider.NextCallAsync();
        var secondTask = gateway.CompleteAsync(Request());

        var secondCall = await provider.NextCallAsync();
        secondCall.Succeed();

        Assert.True((await firstTask).IsSuccess);
        Assert.True((await secondTask).IsSuccess);
        Assert.Equal(2, provider.CallCount);
        Assert.Equal([1], backoff.FailedAttempts);
    }

    [Fact]
    public async Task Cancellation_during_backoff_prevents_another_attempt()
    {
        var retryPolicy = RetryPolicy(maxAttempts: 3);
        var resiliencePolicy = ResiliencePolicy(retryPolicy);
        var resolver = new FixedPolicyResolver(resiliencePolicy);
        using var limiter = new AiProviderAdmissionLimiter(resolver);
        var backoff = new BlockingBackoff();
        var provider = new ScriptedProvider((_, request) => Failure(
            request,
            AiGatewayErrorCode.Timeout,
            isRetryable: true));
        var gateway = Gateway(provider, limiter, resolver, backoff);
        using var cancellation = new CancellationTokenSource();

        var completion = gateway.CompleteAsync(Request(), cancellation.Token);
        await backoff.Started;
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await completion);
        Assert.Single(provider.Requests);
        Assert.Equal([1], backoff.FailedAttempts);
    }

    [Fact]
    public async Task Cancellation_wins_when_provider_ignores_token_and_returns_concurrently()
    {
        var retryPolicy = RetryPolicy(maxAttempts: 3);
        var resiliencePolicy = ResiliencePolicy(retryPolicy);
        var resolver = new FixedPolicyResolver(resiliencePolicy);
        using var limiter = new AiProviderAdmissionLimiter(resolver);
        var provider = new BlockingProvider();
        var backoff = new RecordingBackoff();
        var gateway = Gateway(provider, limiter, resolver, backoff);
        using var cancellation = new CancellationTokenSource();

        var completion = gateway.CompleteAsync(Request(), cancellation.Token);
        var call = await provider.NextCallAsync();
        await cancellation.CancelAsync();
        call.Succeed();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await completion);
        Assert.Equal(cancellation.Token, call.CancellationToken);
        Assert.Empty(backoff.FailedAttempts);
    }

    private static AiGateway Gateway(
        IAiGatewayProvider provider,
        IAiProviderAdmissionLimiter limiter,
        IAiProviderResiliencePolicyResolver resolver,
        IAiProviderRetryBackoff backoff) =>
        new(
            provider,
            auditPublisher: null,
            TimeProvider.System,
            detailedAuditPublisher: null,
            limiter,
            resolver,
            backoff);

    private static AiGatewayRequest Request(
        AiProviderMetadata? provider = null,
        AiGatewayPolicy? policy = null,
        bool omitProvider = false) =>
        new(
            Organization,
            Position,
            Thread,
            Message,
            "Classify this bug.",
            provider: omitProvider ? null : provider ?? Provider,
            policy: policy);

    private static AiProviderRetryPolicy RetryPolicy(int maxAttempts) =>
        new(
            maxAttempts,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(100),
            jitterRatio: 0m);

    private static AiProviderResiliencePolicy ResiliencePolicy(
        AiProviderRetryPolicy retryPolicy,
        int maxConcurrentCalls = 8,
        int queueDepth = 8,
        TimeSpan? maxWait = null) =>
        new(
            new AiProviderRateLimitPolicy(
                maxConcurrentCalls,
                maxCallsPerWindow: 100,
                TimeSpan.FromMinutes(1)),
            new AiProviderQueuePolicy(
                queueDepth,
                maxWait ?? TimeSpan.FromSeconds(1)),
            retryPolicy,
            AiProviderCircuitBreakerPolicy.Default);

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
        AiGatewayErrorCode code,
        bool isRetryable,
        string? message = null) =>
        AiGatewayResponse.Failed(new AiGatewayError(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            code,
            message ?? "Provider failed.",
            isRetryable,
            request.Provider));

    private sealed class FixedPolicyResolver(AiProviderResiliencePolicy policy)
        : IAiProviderResiliencePolicyResolver
    {
        public AiProviderResiliencePolicy Resolve(AiProviderMetadata? provider) => policy;
    }

    private sealed class RecordingBackoff : IAiProviderRetryBackoff
    {
        private readonly List<int> _failedAttempts = [];

        public IReadOnlyList<int> FailedAttempts => _failedAttempts;

        public Task DelayAsync(
            AiProviderRetryPolicy policy,
            int failedAttemptNumber,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _failedAttempts.Add(failedAttemptNumber);
            return Task.CompletedTask;
        }
    }

    private sealed class CallbackBackoff(Func<Task> callback) : IAiProviderRetryBackoff
    {
        private readonly List<int> _failedAttempts = [];

        public IReadOnlyList<int> FailedAttempts => _failedAttempts;

        public async Task DelayAsync(
            AiProviderRetryPolicy policy,
            int failedAttemptNumber,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _failedAttempts.Add(failedAttemptNumber);
            await callback();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class BlockingBackoff : IAiProviderRetryBackoff
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<int> _failedAttempts = [];

        public Task Started => _started.Task;

        public IReadOnlyList<int> FailedAttempts => _failedAttempts;

        public async Task DelayAsync(
            AiProviderRetryPolicy policy,
            int failedAttemptNumber,
            CancellationToken cancellationToken = default)
        {
            _failedAttempts.Add(failedAttemptNumber);
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class ScriptedProvider(
        Func<int, AiGatewayRequest, AiGatewayResponse> respond)
        : IAiGatewayProvider
    {
        private readonly List<AiGatewayRequest> _requests = [];
        private readonly List<CancellationToken> _cancellationTokens = [];
        private readonly List<AiGatewayResponse> _responses = [];

        public IReadOnlyList<AiGatewayRequest> Requests => _requests;

        public IReadOnlyList<CancellationToken> CancellationTokens => _cancellationTokens;

        public IReadOnlyList<AiGatewayResponse> Responses => _responses;

        public Task<AiGatewayResponse> CompleteAsync(
            AiGatewayRequest request,
            CancellationToken cancellationToken)
        {
            _requests.Add(request);
            _cancellationTokens.Add(cancellationToken);
            var response = respond(_requests.Count, request);
            _responses.Add(response);
            return Task.FromResult(response);
        }
    }

    private sealed class BlockingProvider : IAiGatewayProvider
    {
        private readonly Channel<ProviderCall> _calls =
            Channel.CreateUnbounded<ProviderCall>();
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<AiGatewayResponse> CompleteAsync(
            AiGatewayRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            var call = new ProviderCall(request, cancellationToken);
            if (!_calls.Writer.TryWrite(call))
            {
                throw new InvalidOperationException("Could not record provider call.");
            }

            return call.Completion.Task;
        }

        public ValueTask<ProviderCall> NextCallAsync() =>
            _calls.Reader.ReadAsync();
    }

    private sealed class ProviderCall(
        AiGatewayRequest request,
        CancellationToken cancellationToken)
    {
        public TaskCompletionSource<AiGatewayResponse> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public void Succeed() => Completion.TrySetResult(Success(request));
    }

    private sealed class SequenceJitterSource(params decimal[] samples)
        : IAiProviderRetryJitterSource
    {
        private int _index;

        public int CallCount => _index;

        public decimal NextSymmetricSample()
        {
            if (_index >= samples.Length)
            {
                throw new InvalidOperationException("No jitter sample remains.");
            }

            return samples[_index++];
        }
    }

    private sealed class ThrowingJitterSource : IAiProviderRetryJitterSource
    {
        public decimal NextSymmetricSample() =>
            throw new InvalidOperationException("Jitter should not be consumed.");
    }

    private sealed class RecordingTimeProvider : TimeProvider
    {
        private RecordingTimer? _lastTimer;

        public TimeSpan? LastDueTime { get; private set; }

        public bool LastTimerDisposed => _lastTimer?.IsDisposed == true;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            LastDueTime = dueTime;
            _lastTimer = new RecordingTimer();
            return _lastTimer;
        }

        private sealed class RecordingTimer : ITimer
        {
            public bool IsDisposed { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period) => !IsDisposed;

            public void Dispose() => IsDisposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
