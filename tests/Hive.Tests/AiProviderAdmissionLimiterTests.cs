using System.Threading.Channels;
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Infrastructure.Ai;

namespace Hive.Tests;

public sealed class AiProviderAdmissionLimiterTests
{
    private static readonly OrganizationId Organization =
        OrganizationId.From("acme-delivery");
    private static readonly PositionId Position =
        PositionId.From("triage-agent");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public async Task Concurrency_limit_queues_fifo_and_measures_monotonic_wait()
    {
        var clock = new ManualTimeProvider();
        using var limiter = CreateLimiter(clock, Policy(
            maxConcurrentCalls: 1,
            maxCallsPerWindow: 10,
            queueDepth: 2,
            maxWait: TimeSpan.FromSeconds(5)));

        var first = await limiter.AcquireAsync(Request("openai", "primary"));
        var secondTask = limiter
            .AcquireAsync(Request("openai", "secondary"))
            .AsTask();
        var thirdTask = limiter
            .AcquireAsync(Request("openai", "tertiary"))
            .AsTask();

        Assert.True(first.IsAdmitted);
        Assert.Equal(TimeSpan.Zero, first.Lease!.QueueDuration);
        Assert.False(secondTask.IsCompleted);
        Assert.False(thirdTask.IsCompleted);

        clock.Advance(TimeSpan.FromMilliseconds(250));
        first.Lease.Dispose();
        first.Lease.Dispose();

        var second = await secondTask;
        Assert.True(second.IsAdmitted);
        Assert.Equal(TimeSpan.FromMilliseconds(250), second.Lease!.QueueDuration);
        Assert.False(thirdTask.IsCompleted);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        second.Lease.Dispose();

        var third = await thirdTask;
        Assert.True(third.IsAdmitted);
        Assert.Equal(TimeSpan.FromMilliseconds(350), third.Lease!.QueueDuration);
        third.Lease.Dispose();
    }

    [Fact]
    public async Task Sliding_window_counts_admissions_after_concurrency_is_released()
    {
        var clock = new ManualTimeProvider();
        using var limiter = CreateLimiter(clock, Policy(
            maxConcurrentCalls: 2,
            maxCallsPerWindow: 1,
            window: TimeSpan.FromSeconds(1),
            queueDepth: 1,
            maxWait: TimeSpan.FromSeconds(2)));

        var first = await limiter.AcquireAsync(Request("openai", "primary"));
        first.Lease!.Dispose();

        var queuedTask = limiter
            .AcquireAsync(Request("openai", "primary"))
            .AsTask();
        Assert.False(queuedTask.IsCompleted);

        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(queuedTask.IsCompleted);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        var admitted = await queuedTask;

        Assert.True(admitted.IsAdmitted);
        Assert.Equal(TimeSpan.FromSeconds(1), admitted.Lease!.QueueDuration);
        admitted.Lease.Dispose();
    }

    [Fact]
    public async Task Queue_timeout_at_exact_deadline_returns_sanitized_overload()
    {
        var clock = new ManualTimeProvider();
        using var limiter = CreateLimiter(clock, Policy(
            maxConcurrentCalls: 1,
            maxCallsPerWindow: 10,
            queueDepth: 1,
            maxWait: TimeSpan.FromSeconds(1)));

        var active = await limiter.AcquireAsync(Request("openai", "primary"));
        var queuedTask = limiter
            .AcquireAsync(Request("openai", "primary"))
            .AsTask();

        clock.Advance(TimeSpan.FromSeconds(1));
        var rejected = await queuedTask;

        Assert.False(rejected.IsAdmitted);
        Assert.Null(rejected.Lease);
        Assert.Equal(AiGatewayErrorCode.GatewayOverloaded, rejected.Error!.Code);
        Assert.Equal("AI gateway is overloaded.", rejected.Error.Message);
        Assert.True(rejected.Error.IsRetryable);
        active.Lease!.Dispose();
    }

    [Fact]
    public async Task Full_or_disabled_queue_rejects_without_consuming_admission()
    {
        var clock = new ManualTimeProvider();
        using var bounded = CreateLimiter(clock, Policy(
            maxConcurrentCalls: 1,
            maxCallsPerWindow: 10,
            queueDepth: 1,
            maxWait: TimeSpan.FromSeconds(5)));

        var active = await bounded.AcquireAsync(Request("openai", "primary"));
        var queuedTask = bounded
            .AcquireAsync(Request("openai", "primary"))
            .AsTask();
        var overflow = await bounded.AcquireAsync(Request("openai", "primary"));

        Assert.False(overflow.IsAdmitted);
        Assert.Equal(AiGatewayErrorCode.GatewayOverloaded, overflow.Error!.Code);

        active.Lease!.Dispose();
        (await queuedTask).Lease!.Dispose();

        using var disabled = CreateLimiter(clock, Policy(
            maxConcurrentCalls: 1,
            maxCallsPerWindow: 10,
            queueDepth: 0,
            maxWait: TimeSpan.Zero));
        var disabledActive = await disabled.AcquireAsync(Request("anthropic", "primary"));
        var disabledOverflow = await disabled.AcquireAsync(Request("anthropic", "primary"));

        Assert.False(disabledOverflow.IsAdmitted);
        Assert.Equal(
            AiGatewayErrorCode.GatewayOverloaded,
            disabledOverflow.Error!.Code);
        disabledActive.Lease!.Dispose();
    }

    [Fact]
    public async Task Cancellation_removes_waiter_without_consuming_queue_or_window()
    {
        var clock = new ManualTimeProvider();
        using var limiter = CreateLimiter(clock, Policy(
            maxConcurrentCalls: 1,
            maxCallsPerWindow: 10,
            queueDepth: 1,
            maxWait: TimeSpan.FromSeconds(5)));
        using var cancellation = new CancellationTokenSource();

        var active = await limiter.AcquireAsync(Request("openai", "primary"));
        var canceledTask = limiter
            .AcquireAsync(Request("openai", "primary"), cancellation.Token)
            .AsTask();

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await canceledTask);

        var replacementTask = limiter
            .AcquireAsync(Request("openai", "primary"))
            .AsTask();
        Assert.False(replacementTask.IsCompleted);

        active.Lease!.Dispose();
        var replacement = await replacementTask;
        Assert.True(replacement.IsAdmitted);
        replacement.Lease!.Dispose();
    }

    [Fact]
    public async Task Provider_id_isolates_buckets_while_models_share_one_bucket()
    {
        var clock = new ManualTimeProvider();
        using var limiter = CreateLimiter(clock, Policy(
            maxConcurrentCalls: 1,
            maxCallsPerWindow: 10,
            queueDepth: 1,
            maxWait: TimeSpan.FromSeconds(5)));

        var openAi = await limiter.AcquireAsync(Request("openai", "model-a"));
        var sameProviderTask = limiter
            .AcquireAsync(Request("openai", "model-b"))
            .AsTask();
        var anthropic = await limiter.AcquireAsync(Request("anthropic", "model-a"));

        Assert.False(sameProviderTask.IsCompleted);
        Assert.True(anthropic.IsAdmitted);

        openAi.Lease!.Dispose();
        var sameProvider = await sameProviderTask;
        Assert.True(sameProvider.IsAdmitted);

        sameProvider.Lease!.Dispose();
        anthropic.Lease!.Dispose();
    }

    [Fact]
    public async Task Gateway_queues_before_provider_and_rejects_overflow_without_calling_it()
    {
        var clock = new ManualTimeProvider();
        using var limiter = CreateLimiter(clock, Policy(
            maxConcurrentCalls: 1,
            maxCallsPerWindow: 10,
            queueDepth: 1,
            maxWait: TimeSpan.FromSeconds(5)));
        var provider = new BlockingProvider();
        var gateway = new AiGateway(
            provider,
            timeProvider: clock,
            admissionLimiter: limiter);

        var firstTask = gateway.CompleteAsync(Request("openai", "primary"));
        var firstCall = await provider.NextCallAsync();
        var secondTask = gateway.CompleteAsync(Request("openai", "primary"));
        var overflow = await gateway.CompleteAsync(Request("openai", "primary"));

        Assert.False(secondTask.IsCompleted);
        Assert.Equal(1, provider.CallCount);
        Assert.True(overflow.IsFailure);
        Assert.Equal(AiGatewayErrorCode.GatewayOverloaded, overflow.Error!.Code);

        firstCall.Succeed();
        Assert.True((await firstTask).IsSuccess);

        var secondCall = await provider.NextCallAsync();
        Assert.Equal(2, provider.CallCount);
        secondCall.Succeed();
        Assert.True((await secondTask).IsSuccess);
    }

    [Fact]
    public async Task Gateway_releases_concurrency_when_provider_throws()
    {
        var clock = new ManualTimeProvider();
        using var limiter = CreateLimiter(clock, Policy(
            maxConcurrentCalls: 1,
            maxCallsPerWindow: 10,
            queueDepth: 1,
            maxWait: TimeSpan.FromSeconds(5)));
        var provider = new BlockingProvider();
        var gateway = new AiGateway(
            provider,
            timeProvider: clock,
            admissionLimiter: limiter);

        var failingTask = gateway.CompleteAsync(Request("openai", "primary"));
        var failingCall = await provider.NextCallAsync();
        var queuedTask = gateway.CompleteAsync(Request("openai", "primary"));

        failingCall.Fail(new InvalidOperationException("provider failed"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await failingTask);

        var queuedCall = await provider.NextCallAsync();
        queuedCall.Succeed();
        Assert.True((await queuedTask).IsSuccess);
    }

    private static AiProviderAdmissionLimiter CreateLimiter(
        TimeProvider clock,
        AiProviderResiliencePolicy policy) =>
        new(new FixedPolicyResolver(policy), clock);

    private static AiProviderResiliencePolicy Policy(
        int maxConcurrentCalls,
        int maxCallsPerWindow,
        int queueDepth,
        TimeSpan maxWait,
        TimeSpan? window = null) =>
        new(
            new AiProviderRateLimitPolicy(
                maxConcurrentCalls,
                maxCallsPerWindow,
                window ?? TimeSpan.FromMinutes(1)),
            new AiProviderQueuePolicy(queueDepth, maxWait),
            AiProviderRetryPolicy.Default,
            AiProviderCircuitBreakerPolicy.Default);

    private static AiGatewayRequest Request(string providerId, string modelId) =>
        new(
            Organization,
            Position,
            Thread,
            Message,
            "Classify this bug.",
            provider: new AiProviderMetadata(providerId, modelId));

    private static AiGatewayResponse Success(AiGatewayRequest request) =>
        AiGatewayResponse.Succeeded(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            "Done.",
            AiFinishReason.Stop,
            request.Provider);

    private sealed class FixedPolicyResolver(AiProviderResiliencePolicy policy)
        : IAiProviderResiliencePolicyResolver
    {
        public AiProviderResiliencePolicy Resolve(AiProviderMetadata? provider) => policy;
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
            var call = new ProviderCall(request);
            if (!_calls.Writer.TryWrite(call))
            {
                throw new InvalidOperationException("Could not record provider call.");
            }

            return call.Completion.Task;
        }

        public ValueTask<ProviderCall> NextCallAsync() =>
            _calls.Reader.ReadAsync();
    }

    private sealed class ProviderCall(AiGatewayRequest request)
    {
        public TaskCompletionSource<AiGatewayResponse> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Succeed() => Completion.TrySetResult(Success(request));

        public void Fail(Exception exception) => Completion.TrySetException(exception);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow =
            new(2026, 8, 15, 8, 0, 0, TimeSpan.Zero);
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

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            var timer = new ManualTimer(this, callback, state);
            lock (_sync)
            {
                _timers.Add(timer);
                Change(timer, dueTime, period);
            }

            return timer;
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
                _timestamp += duration.Ticks;
            }

            while (TryTakeDueTimer(out var timer))
            {
                timer.Fire();
            }
        }

        private bool TryTakeDueTimer(out ManualTimer timer)
        {
            lock (_sync)
            {
                timer = _timers
                    .Where(candidate =>
                        !candidate.IsDisposed &&
                        candidate.DueAt is { } dueAt &&
                        dueAt <= _timestamp)
                    .OrderBy(candidate => candidate.DueAt)
                    .FirstOrDefault()!;
                if (timer is null)
                {
                    return false;
                }

                timer.PrepareToFire(_timestamp);
                return true;
            }
        }

        private bool Change(
            ManualTimer timer,
            TimeSpan dueTime,
            TimeSpan period)
        {
            lock (_sync)
            {
                if (timer.IsDisposed)
                {
                    return false;
                }

                timer.Period = period;
                timer.DueAt = dueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : checked(_timestamp + dueTime.Ticks);
                return true;
            }
        }

        private void Dispose(ManualTimer timer)
        {
            lock (_sync)
            {
                timer.IsDisposed = true;
                timer.DueAt = null;
            }
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state) : ITimer
        {
            public long? DueAt { get; set; }

            public TimeSpan Period { get; set; } = Timeout.InfiniteTimeSpan;

            public bool IsDisposed { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period) =>
                owner.Change(this, dueTime, period);

            public void Dispose() => owner.Dispose(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void PrepareToFire(long now)
            {
                DueAt = Period == Timeout.InfiniteTimeSpan
                    ? null
                    : checked(now + Period.Ticks);
            }

            public void Fire() => callback(state);
        }
    }
}
