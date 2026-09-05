using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Infrastructure.Ai;

namespace Hive.Tests;

public sealed class AiGatewayMetricsTests
{
    private static readonly OrganizationId Organization =
        OrganizationId.From("acme-delivery");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly AiProviderMetadata Primary = new("openai", "gpt-5-mini");
    private static readonly AiProviderMetadata Secondary =
        new("anthropic", "claude-sonnet");
    private static readonly DateTimeOffset StartedAt =
        new(2026, 9, 4, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Attempt_projection_derives_latency_cost_retry_and_fallback_from_attempt_only()
    {
        var cost = new AiCostMetadata(0.0042m, "EUR", isEstimated: true);
        var pricing = new AiAppliedPricing(
            "price-v1",
            tokenUnit: 1_000_000,
            inputPrice: 1m,
            outputPrice: 2m,
            currency: "EUR");
        var response = AiGatewayResponse.Succeeded(
            Organization,
            Position,
            Thread,
            Message,
            "done",
            AiFinishReason.Stop,
            Secondary,
            cost: cost,
            appliedPricing: pricing);
        var attemptEvent = AiGatewayCostAuditEvent.FromResponse(
            Request(Secondary),
            response,
            StartedAt,
            StartedAt.AddMilliseconds(125),
            new AiGatewayCostAuditAttempt(
                candidateIndex: 2,
                attempt: 1,
                reachedProvider: true,
                queueDuration: TimeSpan.FromMilliseconds(25)));

        var metrics = AiGatewayMetricsProjection.FromAuditEvent(attemptEvent);

        Assert.NotNull(metrics);
        Assert.Equal(Secondary.ProviderId, metrics.ProviderId);
        Assert.Equal(Secondary.ModelId, metrics.ModelId);
        Assert.Equal(AiGatewayCallResult.Succeeded, metrics.Result);
        Assert.Null(metrics.ErrorCode);
        Assert.True(metrics.ReachedProvider);
        Assert.Equal(TimeSpan.FromMilliseconds(25), metrics.QueueLatency);
        Assert.Equal(TimeSpan.FromMilliseconds(100), metrics.ProviderLatency);
        Assert.Same(cost, metrics.Cost);
        Assert.Equal(AiCostStatus.Estimated, metrics.CostStatus);
        Assert.False(metrics.IsRetry);
        Assert.True(metrics.IsFallback);

        var journeyEvent = AiGatewayCostAuditEvent.FromResponse(
            Request(Secondary),
            response,
            StartedAt,
            StartedAt.AddMilliseconds(125));

        Assert.Null(AiGatewayMetricsProjection.FromAuditEvent(journeyEvent));
    }

    [Fact]
    public void Attempt_projection_clamps_provider_latency_and_marks_retries()
    {
        var response = AiGatewayResponse.Failed(new AiGatewayError(
            Organization,
            Position,
            Thread,
            Message,
            AiGatewayErrorCode.Timeout,
            "AI provider timed out.",
            isRetryable: true,
            Primary));
        var auditEvent = AiGatewayCostAuditEvent.FromResponse(
            Request(Primary),
            response,
            StartedAt,
            StartedAt.AddMilliseconds(10),
            new AiGatewayCostAuditAttempt(
                candidateIndex: 0,
                attempt: 2,
                reachedProvider: true,
                queueDuration: TimeSpan.FromMilliseconds(15)));

        var metrics = AiGatewayMetricsProjection.FromAuditEvent(auditEvent);

        Assert.NotNull(metrics);
        Assert.Equal(AiGatewayCallResult.Failed, metrics.Result);
        Assert.Equal(AiGatewayErrorCode.Timeout, metrics.ErrorCode);
        Assert.Equal(TimeSpan.Zero, metrics.ProviderLatency);
        Assert.Equal(AiCostStatus.Unavailable, metrics.CostStatus);
        Assert.Null(metrics.Cost);
        Assert.True(metrics.IsRetry);
        Assert.False(metrics.IsFallback);
    }

    [Fact]
    public async Task Gateway_publishes_one_attempt_metric_and_ignores_the_journey_event()
    {
        var publisher = new RecordingMetricsPublisher();
        var gateway = new AiGateway(
            new DelegatingProvider(request => Success(request)),
            metricsPublisher: publisher);

        var response = await gateway.CompleteAsync(Request(Primary));

        Assert.True(response.IsSuccess);
        var metrics = Assert.Single(publisher.Attempts);
        Assert.Equal(Primary.ProviderId, metrics.ProviderId);
        Assert.Equal(Primary.ModelId, metrics.ModelId);
        Assert.Empty(publisher.FallbackSkips);
        Assert.Empty(publisher.CircuitTransitions);
    }

    [Fact]
    public async Task Retry_and_fallback_sources_increment_only_their_defined_boundaries()
    {
        var publisher = new RecordingMetricsPublisher();
        var call = 0;
        var retryGateway = new AiGateway(
            new DelegatingProvider(request =>
                ++call == 1
                    ? Failure(request, AiGatewayErrorCode.Timeout, isRetryable: true)
                    : Success(request)),
            metricsPublisher: publisher);

        await retryGateway.CompleteAsync(Request(Primary));

        Assert.Equal(2, publisher.Attempts.Count);
        Assert.False(publisher.Attempts[0].IsRetry);
        Assert.True(publisher.Attempts[1].IsRetry);
        Assert.All(publisher.Attempts, metrics => Assert.False(metrics.IsFallback));

        publisher.Clear();
        var fallbackGateway = new AiGateway(
            new DelegatingProvider(request =>
                request.Provider == Primary
                    ? Failure(request, AiGatewayErrorCode.QuotaExceeded, isRetryable: false)
                    : Success(request)),
            metricsPublisher: publisher);

        var response = await fallbackGateway.CompleteAsync(Request(
            Primary,
            Policy(Primary, Secondary)));

        Assert.True(response.IsSuccess);
        Assert.Equal(2, publisher.Attempts.Count);
        Assert.False(publisher.Attempts[0].IsFallback);
        Assert.True(publisher.Attempts[1].IsFallback);
        var skipped = Assert.Single(publisher.FallbackSkips);
        Assert.Equal(Primary.ProviderId, skipped.ProviderId);
        Assert.Equal(AiGatewayFallbackSkipReason.DuplicateCandidate, skipped.Reason);
        Assert.Null(skipped.ErrorCode);
    }

    [Fact]
    public void Circuit_projection_preserves_each_closed_transition_without_correlation()
    {
        var transitions = new[]
        {
            Transition(
                AiProviderCircuitState.Closed,
                AiProviderCircuitState.Open,
                AiProviderCircuitTransitionReason.FailureThresholdReached,
                AiGatewayErrorCode.Timeout),
            Transition(
                AiProviderCircuitState.Open,
                AiProviderCircuitState.HalfOpen,
                AiProviderCircuitTransitionReason.OpenDurationElapsed),
            Transition(
                AiProviderCircuitState.HalfOpen,
                AiProviderCircuitState.Closed,
                AiProviderCircuitTransitionReason.HalfOpenProbeSucceeded),
            Transition(
                AiProviderCircuitState.HalfOpen,
                AiProviderCircuitState.Open,
                AiProviderCircuitTransitionReason.HalfOpenProbeFailed,
                AiGatewayErrorCode.ProviderUnavailable),
        };

        var metrics = transitions
            .Select(AiGatewayMetricsProjection.FromCircuitTransition)
            .ToArray();

        Assert.Equal(4, metrics.Length);
        Assert.Equal(
            transitions.Select(transition => transition.PreviousState),
            metrics.Select(metric => metric.PreviousState));
        Assert.Equal(
            transitions.Select(transition => transition.CurrentState),
            metrics.Select(metric => metric.CurrentState));
        Assert.Equal(
            transitions.Select(transition => transition.Reason),
            metrics.Select(metric => metric.Reason));
        Assert.Equal(
            transitions.Select(transition => transition.ErrorCode),
            metrics.Select(metric => metric.ErrorCode));
    }

    [Fact]
    public async Task Cancellation_and_provider_exception_without_source_events_publish_no_metrics()
    {
        var publisher = new RecordingMetricsPublisher();
        var throwingGateway = new AiGateway(
            new DelegatingProvider(_ => throw new InvalidOperationException("transport")),
            metricsPublisher: publisher);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await throwingGateway.CompleteAsync(Request(Primary)));
        Assert.Empty(publisher.Attempts);
        Assert.Empty(publisher.FallbackSkips);
        Assert.Empty(publisher.CircuitTransitions);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await throwingGateway.CompleteAsync(
                Request(Primary),
                cancellation.Token));
        Assert.Empty(publisher.Attempts);
    }

    [Fact]
    public void Metric_snapshots_exclude_high_cardinality_and_free_text_fields()
    {
        var forbiddenNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "OrganizationId",
            "PositionId",
            "ThreadId",
            "MessageId",
            "DirectiveId",
            "OccurredAt",
            "StartedAt",
            "CompletedAt",
            "Content",
            "Metadata",
            "Message",
            "Diagnostics",
        };

        foreach (var type in new[]
        {
            typeof(AiGatewayAttemptMetrics),
            typeof(AiGatewayFallbackSkipMetrics),
            typeof(AiProviderCircuitTransitionMetrics),
        })
        {
            Assert.DoesNotContain(
                type.GetProperties(),
                property => forbiddenNames.Contains(property.Name));
        }

        Assert.Equal(
            new[] { "ModelId", "ProviderId" },
            StringProperties(typeof(AiGatewayAttemptMetrics)));
        Assert.Equal(
            new[] { "ModelId", "ProviderId" },
            StringProperties(typeof(AiGatewayFallbackSkipMetrics)));
        Assert.Equal(
            new[] { "ProviderId" },
            StringProperties(typeof(AiProviderCircuitTransitionMetrics)));
    }

    private static string[] StringProperties(Type type) =>
        type.GetProperties()
            .Where(property => property.PropertyType == typeof(string))
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static AiProviderCircuitTransition Transition(
        AiProviderCircuitState previousState,
        AiProviderCircuitState currentState,
        AiProviderCircuitTransitionReason reason,
        AiGatewayErrorCode? errorCode = null) =>
        new(
            Organization,
            Position,
            Thread,
            Message,
            Primary.ProviderId,
            previousState,
            currentState,
            StartedAt,
            reason,
            errorCode);

    private static AiGatewayRequest Request(
        AiProviderMetadata provider,
        AiGatewayPolicy? policy = null) =>
        new(
            Organization,
            Position,
            Thread,
            Message,
            "Classify this work item.",
            provider: provider,
            policy: policy);

    private static AiGatewayPolicy Policy(params AiProviderMetadata[] fallback) =>
        new(
            new[] { Primary, Secondary },
            hasAvailableBudget: true,
            maxOutputTokens: null,
            maxTimeout: null,
            allowedProcessingModes: null,
            authorizedTools: null,
            fallback);

    private static AiGatewayResponse Success(AiGatewayRequest request) =>
        AiGatewayResponse.Succeeded(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            "done",
            AiFinishReason.Stop,
            request.Provider);

    private static AiGatewayResponse Failure(
        AiGatewayRequest request,
        AiGatewayErrorCode code,
        bool isRetryable) =>
        AiGatewayResponse.Failed(new AiGatewayError(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            code,
            "AI gateway request failed.",
            isRetryable,
            request.Provider));

    private sealed class DelegatingProvider(Func<AiGatewayRequest, AiGatewayResponse> complete)
        : IAiGatewayProvider
    {
        public Task<AiGatewayResponse> CompleteAsync(
            AiGatewayRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(complete(request));
        }
    }

    private sealed class RecordingMetricsPublisher : IAiGatewayMetricsPublisher
    {
        public List<AiGatewayAttemptMetrics> Attempts { get; } = [];

        public List<AiGatewayFallbackSkipMetrics> FallbackSkips { get; } = [];

        public List<AiProviderCircuitTransitionMetrics> CircuitTransitions { get; } = [];

        public void Publish(AiGatewayAttemptMetrics metrics) => Attempts.Add(metrics);

        public void Publish(AiGatewayFallbackSkipMetrics metrics) =>
            FallbackSkips.Add(metrics);

        public void Publish(AiProviderCircuitTransitionMetrics metrics) =>
            CircuitTransitions.Add(metrics);

        public void Clear()
        {
            Attempts.Clear();
            FallbackSkips.Clear();
            CircuitTransitions.Clear();
        }
    }
}
