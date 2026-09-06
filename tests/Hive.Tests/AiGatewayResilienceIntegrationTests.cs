using System.Collections.Concurrent;
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Infrastructure.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests;

// US-F1-05-T12: real DI/configuration/resilience pipeline, with no external provider.
public sealed class AiGatewayResilienceIntegrationTests
{
    private static readonly AiProviderMetadata Primary = new("primary", "model-a");
    private static readonly AiProviderMetadata Secondary = new("secondary", "model-b");
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_limit_queues_completes_and_reports_overflow_without_calling_adapter(
        bool windowLimited)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rig = new Rig(async (request, token) =>
        {
            await release.Task.WaitAsync(token);
            return Success(request);
        }, ("primary:RateLimit:MaxCallsPerWindow", windowLimited ? "1" : "100"));
        await rig.StartAsync();
        using var cancellation = new CancellationTokenSource(Watchdog);
        var first = rig.Gateway.CompleteAsync(Request(), cancellation.Token);
        Assert.Single(rig.Provider.Requests);
        if (windowLimited)
        {
            release.SetResult();
            Assert.True((await first.WaitAsync(Watchdog)).IsSuccess);
        }

        var queuedRequest = Request();
        var queued = rig.Gateway.CompleteAsync(queuedRequest, cancellation.Token);
        Assert.False(queued.IsCompleted);
        Assert.Single(rig.Provider.Requests);
        var overflowRequest = Request();
        var overflow = await rig.Gateway.CompleteAsync(overflowRequest, cancellation.Token);
        Assert.Equal(AiGatewayErrorCode.GatewayOverloaded, overflow.Error!.Code);
        Assert.True(overflow.Error.IsRetryable);
        Assert.Equal("AI gateway is overloaded.", overflow.Error.Message);
        Assert.Single(rig.Provider.Requests);

        var elapsed = TimeSpan.FromSeconds(windowLimited ? 10 : 2);
        rig.Clock.Advance(elapsed);
        if (!windowLimited)
            release.SetResult();
        Assert.True((await first.WaitAsync(Watchdog)).IsSuccess);
        Assert.True((await queued.WaitAsync(Watchdog)).IsSuccess);
        Assert.Equal(2, rig.Provider.Requests.Count);
        var queuedEvent = Assert.Single(rig.Attempts.Where(e => e.MessageId == queuedRequest.MessageId));
        Assert.Equal(elapsed, queuedEvent.QueueDuration);
        var rejected = Assert.Single(rig.Attempts.Where(e => e.MessageId == overflowRequest.MessageId));
        Assert.False(rejected.ReachedProvider);
        Assert.Null(rejected.QueueDuration);
        Assert.Null(rejected.Cost);
        Assert.Equal("gateway-overloaded", rig.Envelope(overflowRequest).RejectionReason);
        rig.AssertTelemetry(attempts: 3, journeys: 3);
        Assert.Equal(0.08m, rig.Observations.Metrics.Sum(m => m.Cost?.Amount ?? 0));
        Assert.Contains(rig.Observations.Metrics, m => m.QueueLatency == elapsed && m.ProviderLatency == TimeSpan.Zero);
    }

    [Fact]
    public async Task Consecutive_failures_open_circuit_fallback_serves_and_half_open_probe_recovers()
    {
        var primaryCalls = 0;
        var releaseProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rig = new Rig(async (request, token) =>
        {
            if (request.Provider == Primary)
            {
                if (Interlocked.Increment(ref primaryCalls) <= 2)
                    return Failure(request, AiGatewayErrorCode.QuotaExceeded);
                await releaseProbe.Task.WaitAsync(token);
            }
            return Success(request);
        });
        await rig.StartAsync();
        using var cancellation = new CancellationTokenSource(Watchdog);
        for (var i = 0; i < 2; i++)
            Assert.Equal(Secondary, (await rig.Gateway.CompleteAsync(Request(fallback: true))).Provider);
        Assert.Equal(2, primaryCalls);

        var openRequest = Request(fallback: true);
        Assert.Equal(Secondary, (await rig.Gateway.CompleteAsync(openRequest)).Provider);
        var openAttempt = rig.Envelope(openRequest).Journey[0];
        Assert.Equal(AiGatewayErrorReason.CircuitOpen, openAttempt.ErrorReason);
        Assert.False(openAttempt.ReachedProvider);
        Assert.Equal(2, primaryCalls);

        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        var probeRequest = Request(fallback: true);
        var probe = rig.Gateway.CompleteAsync(probeRequest, cancellation.Token);
        Assert.False(probe.IsCompleted);
        Assert.Equal(3, primaryCalls);
        // The single half-open probe owns the primary; concurrent traffic uses fallback.
        Assert.Equal(Secondary, (await rig.Gateway.CompleteAsync(Request(fallback: true))).Provider);
        Assert.Equal(3, primaryCalls);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(30));
        releaseProbe.SetResult();
        Assert.Equal(Primary, (await probe.WaitAsync(Watchdog)).Provider);
        Assert.Equal(Primary, (await rig.Gateway.CompleteAsync(Request(fallback: true))).Provider);
        Assert.Equal(4, primaryCalls);
        Assert.Equal(TimeSpan.FromMilliseconds(30), Assert.Single(rig.Envelope(probeRequest).Journey).Duration);

        Assert.Equal(new[]
        {
            AiProviderCircuitState.Open, AiProviderCircuitState.HalfOpen, AiProviderCircuitState.Closed,
        }, rig.Observations.Transitions.Select(t => t.CurrentState));
        Assert.All(rig.Observations.Transitions, t => Assert.Equal(Primary.ProviderId, t.ProviderId));
        Assert.Equal(rig.Observations.Transitions.Select(t => t.CurrentState),
            rig.Observations.CircuitMetrics.Select(m => m.CurrentState));
        rig.AssertTelemetry(attempts: 10, journeys: 6);
        Assert.Equal(4, rig.Observations.Metrics.Count(m => m.IsFallback));
        Assert.Equal(2, rig.Observations.Metrics.Count(m => !m.ReachedProvider));
    }

    [Fact]
    public async Task Retry_backoff_then_fallback_preserves_attempt_cost_and_journey_correlation()
    {
        using var rig = new Rig((request, _) =>
        {
            return Task.FromResult(request.Provider == Primary
                ? Failure(request, AiGatewayErrorCode.Timeout, retryable: true, cost: 0.01m)
                : Success(request));
        }, ("primary:Retry:MaxAttempts", "2"), ("primary:CircuitBreaker:FailureThreshold", "10"));
        await rig.StartAsync();
        using var cancellation = new CancellationTokenSource(Watchdog);
        var request = Request(fallback: true);
        var pending = rig.Gateway.CompleteAsync(request, cancellation.Token);
        Assert.False(pending.IsCompleted);
        Assert.Single(rig.Provider.Requests);
        Assert.Single(rig.Attempts);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(99));
        Assert.False(pending.IsCompleted);
        Assert.Single(rig.Provider.Requests);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(1));
        var response = await pending.WaitAsync(Watchdog);
        Assert.Equal(Secondary, response.Provider);
        Assert.Equal(new[] { 0, 0, 1 }, rig.Attempts.Select(e => e.CandidateIndex!.Value));
        Assert.Equal(new[] { 1, 2, 1 }, rig.Attempts.Select(e => e.Attempt!.Value));
        var calls = rig.Provider.Requests.ToArray();
        Assert.Same(calls[0], calls[1]);
        Assert.All(calls, call => Assert.Equal(request.MessageId, call.MessageId));
        Assert.Equal(TimeSpan.FromMilliseconds(100), rig.Envelope(request).Duration);
        rig.AssertTelemetry(attempts: 3, journeys: 1);
        Assert.Single(rig.Observations.Metrics.Where(m => m.IsRetry));
        Assert.Single(rig.Observations.Metrics.Where(m => m.IsFallback));
        Assert.Equal(0.06m, rig.Observations.Metrics.Sum(m => m.Cost?.Amount ?? 0));
    }

    [Fact]
    public async Task Exhausted_chain_preserves_last_error_and_audits_terminal_reason()
    {
        using var rig = new Rig((request, _) => Task.FromResult(
            Failure(request, request.Provider == Primary
                ? AiGatewayErrorCode.QuotaExceeded : AiGatewayErrorCode.ProviderUnavailable)));
        await rig.StartAsync();
        var request = Request(fallback: true);
        var response = await rig.Gateway.CompleteAsync(request).WaitAsync(Watchdog);
        Assert.Equal(AiGatewayErrorCode.ProviderUnavailable, response.Error!.Code);
        Assert.Equal(AiGatewayErrorReason.FallbackExhausted, response.Error.Reason);
        Assert.Equal(Secondary, response.Error.Provider);
        Assert.Equal("AI provider failed.", response.Error.Message);
        var envelope = rig.Envelope(request);
        Assert.Equal("fallback-exhausted", envelope.RejectionReason);
        Assert.Equal(AiGatewayErrorReason.FallbackExhausted, envelope.Error!.Reason);
        Assert.Equal(new[] { Primary.ProviderId, Secondary.ProviderId }, envelope.Journey.Select(a => a.ProviderId));
        Assert.All(envelope.Journey, a => Assert.True(a.ReachedProvider));
        rig.AssertTelemetry(attempts: 2, journeys: 1);
        Assert.All(rig.Observations.Metrics, m => Assert.Equal(AiGatewayCallResult.Failed, m.Result));
        Assert.Empty(rig.Observations.Metrics.Where(m => m.IsRetry));
        Assert.Single(rig.Observations.Metrics.Where(m => m.IsFallback));
    }

    private static AiGatewayRequest Request(bool fallback = false) => new(
        OrganizationId.From("acme-delivery"), PositionId.From("triage-agent"),
        ThreadId.From(Guid.NewGuid()), MessageId.From(Guid.NewGuid()), "Classify this bug.",
        provider: Primary,
        policy: fallback ? new AiGatewayPolicy([Primary, Secondary], true, null, null, null, null, [Secondary]) : null);

    private static AiGatewayResponse Success(AiGatewayRequest request) => AiGatewayResponse.Succeeded(
        request.OrganizationId, request.PositionId, request.ThreadId, request.MessageId,
        "Done.", AiFinishReason.Stop, request.Provider, cost: new AiCostMetadata(0.04m, "EUR", true));

    private static AiGatewayResponse Failure(AiGatewayRequest request, AiGatewayErrorCode code,
        bool retryable = false, decimal? cost = null) => AiGatewayResponse.Failed(new AiGatewayError(
        request.OrganizationId, request.PositionId, request.ThreadId, request.MessageId,
        code, "AI provider failed.", retryable, request.Provider,
        cost is null ? null : new AiGatewayFailureDiagnostics(cost: new AiCostMetadata(cost.Value, "EUR", true))));

    private sealed class Rig : IDisposable
    {
        private readonly IHost _host;
        public ManualClock Clock { get; } = new();
        public Observations Observations { get; } = new();
        public ScriptedProvider Provider { get; }
        public IAiGateway Gateway => _host.Services.GetRequiredService<IAiGateway>();
        public AiGatewayCostAuditEvent[] Attempts => Observations.Events.Where(e => e.Scope == AiGatewayCostAuditScope.Attempt).ToArray();

        public Rig(Func<AiGatewayRequest, CancellationToken, Task<AiGatewayResponse>> complete,
            params (string Key, string Value)[] overrides)
        {
            Provider = new ScriptedProvider(complete);
            var values = new Dictionary<string, string?>();
            foreach (var provider in new[] { "primary", "secondary" })
            {
                foreach (var (key, value) in new[]
                {
                    ("RateLimit:MaxConcurrentCalls", "1"), ("RateLimit:MaxCallsPerWindow", "100"),
                    ("RateLimit:Window", "00:00:10"), ("Queue:MaxDepth", "1"), ("Queue:MaxWait", "00:01:00"),
                    ("Retry:MaxAttempts", "1"), ("Retry:InitialBackoff", "00:00:00.1000000"),
                    ("Retry:MaxBackoff", "00:00:00.1000000"), ("Retry:JitterRatio", "0"),
                    ("CircuitBreaker:FailureThreshold", "2"), ("CircuitBreaker:OpenDuration", "00:00:10"),
                }) values[$"Hive:AiGateway:Providers:{provider}:{key}"] = value;
            }
            foreach (var (key, value) in overrides) values[$"Hive:AiGateway:Providers:{key}"] = value;
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            builder.Services.AddSingleton<TimeProvider>(Clock);
            builder.Services.AddSingleton<IAiGatewayProvider>(Provider);
            builder.Services.AddSingleton<IAiGatewayAuditPublisher>(Observations);
            builder.Services.AddSingleton<IAiGatewayDetailedAuditPublisher>(Observations);
            builder.Services.AddSingleton<IAiGatewayMetricsPublisher>(Observations);
            builder.Services.AddSingleton<IAiProviderCircuitTransitionPublisher>(Observations);
            builder.Services.AddHiveAiGateway(configuration);
            _host = builder.Build();
        }

        public async Task StartAsync()
        {
            await _host.StartAsync();
            Assert.Null(_host.Services.GetService<IChatClient>());
            Assert.IsType<AiGateway>(Gateway);
            Assert.IsType<AiProviderAdmissionLimiter>(_host.Services.GetRequiredService<IAiProviderAdmissionLimiter>());
            Assert.IsType<AiProviderCircuitBreaker>(_host.Services.GetRequiredService<IAiProviderCircuitBreaker>());
            Assert.IsType<AiProviderRetryBackoff>(_host.Services.GetRequiredService<IAiProviderRetryBackoff>());
        }

        public AiGatewayAuditEnvelope Envelope(AiGatewayRequest request) =>
            Assert.Single(Observations.Envelopes.Where(e => e.MessageId == request.MessageId));

        public void AssertTelemetry(int attempts, int journeys)
        {
            Assert.Equal(attempts, Attempts.Length);
            Assert.Equal(attempts, Attempts.Select(a =>
                (a.OrganizationId, a.PositionId, a.ThreadId, a.MessageId, a.AttemptId)).Distinct().Count());
            Assert.Equal(attempts, Observations.Metrics.Count);
            Assert.Equal(journeys, Observations.Envelopes.Count);
            Assert.Equal(journeys, Observations.Events.Count(e => e.Scope == AiGatewayCostAuditScope.Journey));
            Assert.Equal(attempts + journeys, Observations.Events.Count);
            foreach (var envelope in Observations.Envelopes)
            {
                var events = Attempts.Where(e => e.MessageId == envelope.MessageId).ToArray();
                Assert.Equal(events.Select(e => e.AttemptId), envelope.Journey.Select(a => a.AttemptId));
                Assert.All(events, e =>
                {
                    Assert.Equal(envelope.OrganizationId, e.OrganizationId);
                    Assert.Equal(envelope.PositionId, e.PositionId);
                    Assert.Equal(envelope.ThreadId, e.ThreadId);
                });
            }
            // Compare multisets: independent concurrent journeys can interleave publication.
            var expectedMetrics = Attempts.Select(e => AiGatewayMetricsProjection.FromAuditEvent(e)!)
                .GroupBy(m => m).ToDictionary(g => g.Key, g => g.Count());
            var actualMetrics = Observations.Metrics.GroupBy(m => m).ToDictionary(g => g.Key, g => g.Count());
            Assert.Equal(expectedMetrics.Count, actualMetrics.Count);
            foreach (var (metric, count) in expectedMetrics)
            {
                Assert.True(actualMetrics.TryGetValue(metric, out var actualCount));
                Assert.Equal(count, actualCount);
            }
        }

        public void Dispose() => _host.Dispose();
    }

    private sealed class ScriptedProvider(Func<AiGatewayRequest, CancellationToken, Task<AiGatewayResponse>> complete)
        : IAiGatewayProvider
    {
        public ConcurrentQueue<AiGatewayRequest> Requests { get; } = new();
        public Task<AiGatewayResponse> CompleteAsync(AiGatewayRequest request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            return complete(request, cancellationToken);
        }
    }

    private sealed class Observations : IAiGatewayAuditPublisher, IAiGatewayDetailedAuditPublisher,
        IAiGatewayMetricsPublisher, IAiProviderCircuitTransitionPublisher
    {
        public ConcurrentQueue<AiGatewayCostAuditEvent> Events { get; } = new();
        public ConcurrentQueue<AiGatewayAuditEnvelope> Envelopes { get; } = new();
        public ConcurrentQueue<AiGatewayAttemptMetrics> Metrics { get; } = new();
        public ConcurrentQueue<AiProviderCircuitTransition> Transitions { get; } = new();
        public ConcurrentQueue<AiProviderCircuitTransitionMetrics> CircuitMetrics { get; } = new();
        public void Publish(AiGatewayCostAuditEvent value) => Events.Enqueue(value);
        public void Publish(AiGatewayAuditEnvelope value) => Envelopes.Enqueue(value);
        public void Publish(AiGatewayAttemptMetrics value) => Metrics.Enqueue(value);
        public void Publish(AiGatewayFallbackSkipMetrics value) => Assert.Fail("No candidate should be skipped.");
        public void Publish(AiProviderCircuitTransition value) => Transitions.Enqueue(value);
        public void Publish(AiProviderCircuitTransitionMetrics value) => CircuitMetrics.Enqueue(value);
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_sync) return _ticks; }
        public override DateTimeOffset GetUtcNow() =>
            new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_sync)
            {
                var timer = new ManualTimer(this, callback, state);
                _timers.Add(timer);
                timer.Change(dueTime, period);
                return timer;
            }
        }

        public void Advance(TimeSpan elapsed)
        {
            lock (_sync) _ticks += elapsed.Ticks;
            while (true)
            {
                ManualTimer? timer;
                lock (_sync)
                {
                    timer = _timers.Where(t => t.DueAt <= _ticks).OrderBy(t => t.DueAt).FirstOrDefault();
                    if (timer is null) return;
                    timer.DueAt = null;
                }
                timer.Fire();
            }
        }

        private sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state) : ITimer
        {
            public long? DueAt { get; set; }
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                lock (owner._sync)
                {
                    if (_disposed) return false;
                    DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._ticks + dueTime.Ticks;
                    return true;
                }
            }
            public void Fire() => callback(state);
            public void Dispose() { lock (owner._sync) { _disposed = true; DueAt = null; } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
