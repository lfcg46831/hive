using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Infrastructure.Ai;

namespace Hive.Tests;

/// <summary>
/// US-F1-05-T08: one cost/audit event per gateway attempt, plus the ordered journey on
/// the single detailed envelope of the call.
/// </summary>
public sealed class AiGatewayAttemptAuditTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    private static readonly OrganizationId Organization =
        OrganizationId.From("acme-delivery");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private static readonly AiProviderMetadata Primary = new("openai", "gpt-5-mini");
    private static readonly AiProviderMetadata Secondary = new("anthropic", "claude-sonnet");

    [Fact]
    public void Scope_contract_is_closed_and_canonical()
    {
        Assert.Equal(
            "attempt",
            AiGatewayCostAuditScopeContract.ToWireValue(AiGatewayCostAuditScope.Attempt));
        Assert.Equal(
            "journey",
            AiGatewayCostAuditScopeContract.ToWireValue(AiGatewayCostAuditScope.Journey));
        Assert.Equal(
            AiGatewayCostAuditScope.Attempt,
            AiGatewayCostAuditScopeContract.ParseWireValue("attempt"));

        Assert.False(AiGatewayCostAuditScopeContract.TryParseWireValue("Attempt", out _));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AiGatewayCostAuditScopeContract.ToWireValue((AiGatewayCostAuditScope)9));
        Assert.Throws<ArgumentException>(
            () => AiGatewayCostAuditScopeContract.ParseWireValue("call"));
    }

    [Fact]
    public void Attempt_identity_is_deterministic_and_validated()
    {
        var attempt = new AiGatewayCostAuditAttempt(
            candidateIndex: 2,
            attempt: 3,
            reachedProvider: true,
            TimeSpan.FromMilliseconds(40));

        Assert.Equal("2-3", attempt.AttemptId);
        Assert.Equal(
            attempt.AttemptId,
            new AiGatewayCostAuditAttempt(2, 3, reachedProvider: false).AttemptId);
        Assert.Equal("0-1", AiGatewayCostAuditAttempt.FormatAttemptId(0, 1));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AiGatewayCostAuditAttempt(-1, 1, reachedProvider: true));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AiGatewayCostAuditAttempt(0, 0, reachedProvider: true));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AiGatewayCostAuditAttempt(
                0,
                1,
                reachedProvider: true,
                TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public void An_attempt_snapshot_refuses_contradictory_outcomes()
    {
        Assert.Throws<ArgumentException>(() => new AiGatewayAuditAttemptSnapshot(
            candidateIndex: 0,
            attempt: 1,
            reachedProvider: false,
            AiGatewayCallResult.Succeeded,
            TimeSpan.FromMilliseconds(10)));
        Assert.Throws<ArgumentException>(() => new AiGatewayAuditAttemptSnapshot(
            candidateIndex: 0,
            attempt: 1,
            reachedProvider: true,
            AiGatewayCallResult.Failed,
            TimeSpan.FromMilliseconds(10)));
        Assert.Throws<ArgumentException>(() => new AiGatewayAuditAttemptSnapshot(
            candidateIndex: 0,
            attempt: 1,
            reachedProvider: true,
            AiGatewayCallResult.Succeeded,
            TimeSpan.FromMilliseconds(10),
            errorCode: AiGatewayErrorCode.Timeout));
    }

    [Fact]
    public async Task A_successful_first_attempt_publishes_one_attempt_and_one_journey_event()
    {
        var cost = new AiCostMetadata(0.0004m, "USD", isEstimated: true);
        var audit = new RecordingAuditPublisher();
        var gateway = Gateway(
            new ScriptedProvider((_, request) => Success(request, cost)),
            auditPublisher: audit);

        var response = await gateway.CompleteAsync(Request());

        Assert.True(response.IsSuccess);
        var attempt = Assert.Single(Attempts(audit));
        Assert.Equal("0-1", attempt.AttemptId);
        Assert.Equal(0, attempt.CandidateIndex);
        Assert.Equal(1, attempt.Attempt);
        Assert.True(attempt.ReachedProvider);
        Assert.Equal(TimeSpan.Zero, attempt.QueueDuration);
        Assert.Equal(Primary, attempt.Provider);
        Assert.Equal(cost, attempt.Cost);

        var journey = Assert.Single(Journeys(audit));
        Assert.Null(journey.AttemptId);
        Assert.Null(journey.CandidateIndex);
        Assert.Null(journey.QueueDuration);
        Assert.Null(journey.ReachedProvider);
        Assert.Equal(cost, journey.Cost);
    }

    [Fact]
    public async Task Exhausted_retries_publish_one_event_per_attempt_of_the_same_candidate()
    {
        var audit = new RecordingAuditPublisher();
        var gateway = Gateway(
            new ScriptedProvider((_, request) =>
                Failure(request, AiGatewayErrorCode.Timeout, isRetryable: true)),
            resolver: FixedResolver(maxAttempts: 3),
            auditPublisher: audit);

        var response = await gateway.CompleteAsync(Request());

        Assert.True(response.IsFailure);
        Assert.Equal(
            new[] { "0-1", "0-2", "0-3" },
            Attempts(audit).Select(@event => @event.AttemptId));
        Assert.All(Attempts(audit), @event =>
        {
            Assert.Equal(0, @event.CandidateIndex);
            Assert.True(@event.ReachedProvider);
            Assert.Equal(AiGatewayErrorCode.Timeout, @event.ErrorCode);
            Assert.Null(@event.Cost);
        });
        Assert.Single(Journeys(audit));
    }

    [Fact]
    public async Task A_fallback_candidate_advances_the_chain_index_of_the_attempt_identity()
    {
        var audit = new RecordingAuditPublisher();
        var detailed = new RecordingDetailedAuditPublisher();
        var gateway = Gateway(
            new ScriptedProvider((_, request) => request.Provider == Primary
                ? Failure(request, AiGatewayErrorCode.QuotaExceeded, isRetryable: false)
                : Success(request)),
            resolver: FixedResolver(maxAttempts: 2),
            auditPublisher: audit,
            detailedAuditPublisher: detailed);

        var response = await gateway.CompleteAsync(Request(Policy(Secondary)));

        Assert.True(response.IsSuccess);
        Assert.Equal(
            new[] { "0-1", "1-1" },
            Attempts(audit).Select(@event => @event.AttemptId));
        Assert.Equal(
            new[] { Primary, Secondary },
            Attempts(audit).Select(@event => @event.Provider));

        var envelope = Assert.Single(detailed.Envelopes);
        Assert.Equal(
            new[] { "0-1", "1-1" },
            envelope.Journey.Select(entry => entry.AttemptId));
        Assert.Equal(
            new[] { Primary.ProviderId, Secondary.ProviderId },
            envelope.Journey.Select(entry => entry.ProviderId));
        Assert.Equal(
            new[] { AiGatewayCallResult.Failed, AiGatewayCallResult.Succeeded },
            envelope.Journey.Select(entry => entry.Result));
        Assert.Equal(
            AiGatewayErrorCode.QuotaExceeded,
            envelope.Journey[0].ErrorCode);
        Assert.Null(envelope.Journey[1].ErrorCode);
    }

    [Fact]
    public async Task An_open_circuit_publishes_an_attempt_that_never_reached_the_provider()
    {
        var audit = new RecordingAuditPublisher();
        var detailed = new RecordingDetailedAuditPublisher();
        var provider = new ScriptedProvider((_, request) => Success(request));
        var gateway = Gateway(
            provider,
            auditPublisher: audit,
            detailedAuditPublisher: detailed,
            circuitBreaker: new OpenCircuitBreaker());

        var response = await gateway.CompleteAsync(Request());

        Assert.True(response.IsFailure);
        Assert.Empty(provider.Requests);
        var attempt = Assert.Single(Attempts(audit));
        Assert.Equal("0-1", attempt.AttemptId);
        Assert.False(attempt.ReachedProvider);
        Assert.Null(attempt.QueueDuration);
        Assert.Equal(AiGatewayErrorCode.ProviderUnavailable, attempt.ErrorCode);

        var entry = Assert.Single(Assert.Single(detailed.Envelopes).Journey);
        Assert.False(entry.ReachedProvider);
        Assert.Null(entry.QueueDuration);
        Assert.Equal(AiGatewayErrorReason.CircuitOpen, entry.ErrorReason);
    }

    [Fact]
    public async Task Local_saturation_publishes_attempts_without_a_queue_measurement()
    {
        var audit = new RecordingAuditPublisher();
        var provider = new ScriptedProvider((_, request) => Success(request));
        var gateway = Gateway(
            provider,
            resolver: FixedResolver(maxAttempts: 2),
            limiter: new RejectingAdmissionLimiter(),
            auditPublisher: audit);

        var response = await gateway.CompleteAsync(Request());

        Assert.True(response.IsFailure);
        Assert.Empty(provider.Requests);
        Assert.Equal(
            new[] { "0-1", "0-2" },
            Attempts(audit).Select(@event => @event.AttemptId));
        Assert.All(Attempts(audit), @event =>
        {
            Assert.False(@event.ReachedProvider);
            Assert.Null(@event.QueueDuration);
            Assert.Equal(AiGatewayErrorCode.GatewayOverloaded, @event.ErrorCode);
        });
    }

    [Fact]
    public async Task A_pre_call_rejection_publishes_only_a_journey_event_with_an_empty_journey()
    {
        var audit = new RecordingAuditPublisher();
        var detailed = new RecordingDetailedAuditPublisher();
        var provider = new ScriptedProvider((_, request) => Success(request));
        var gateway = Gateway(
            provider,
            auditPublisher: audit,
            detailedAuditPublisher: detailed);

        var response = await gateway.CompleteAsync(Request(new AiGatewayPolicy(
            new[] { Primary },
            hasAvailableBudget: false,
            maxOutputTokens: null,
            maxTimeout: null,
            allowedProcessingModes: null,
            authorizedTools: null,
            fallback: null)));

        Assert.True(response.IsFailure);
        Assert.Empty(provider.Requests);
        Assert.Empty(Attempts(audit));
        var journey = Assert.Single(Journeys(audit));
        Assert.Equal(AiGatewayErrorCode.BudgetInsufficient, journey.ErrorCode);
        Assert.Empty(Assert.Single(detailed.Envelopes).Journey);
    }

    [Fact]
    public async Task Caller_cancellation_publishes_no_attempt_and_no_envelope()
    {
        using var cancellation = new CancellationTokenSource();
        var audit = new RecordingAuditPublisher();
        var detailed = new RecordingDetailedAuditPublisher();
        var gateway = Gateway(
            new ScriptedProvider((_, request) =>
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
                return Success(request);
            }),
            auditPublisher: audit,
            detailedAuditPublisher: detailed);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gateway.CompleteAsync(Request(), cancellation.Token));

        Assert.Empty(audit.Events);
        Assert.Empty(detailed.Envelopes);
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    private static IEnumerable<AiGatewayCostAuditEvent> Attempts(
        RecordingAuditPublisher audit) =>
        audit.Events.Where(@event => @event.Scope == AiGatewayCostAuditScope.Attempt);

    private static IEnumerable<AiGatewayCostAuditEvent> Journeys(
        RecordingAuditPublisher audit) =>
        audit.Events.Where(@event => @event.Scope == AiGatewayCostAuditScope.Journey);

    private AiGateway Gateway(
        IAiGatewayProvider provider,
        IAiProviderResiliencePolicyResolver? resolver = null,
        IAiProviderAdmissionLimiter? limiter = null,
        IAiGatewayAuditPublisher? auditPublisher = null,
        IAiGatewayDetailedAuditPublisher? detailedAuditPublisher = null,
        IAiProviderCircuitBreaker? circuitBreaker = null)
    {
        resolver ??= FixedResolver();
        return new AiGateway(
            provider,
            auditPublisher,
            TimeProvider.System,
            detailedAuditPublisher,
            limiter ?? Track(new AiProviderAdmissionLimiter(resolver)),
            resolver,
            new ImmediateBackoff(),
            circuitBreaker);
    }

    private T Track<T>(T disposable)
        where T : IDisposable
    {
        _disposables.Add(disposable);
        return disposable;
    }

    private static IAiProviderResiliencePolicyResolver FixedResolver(int maxAttempts = 1) =>
        new FixedPolicyResolver(new AiProviderResiliencePolicy(
            AiProviderRateLimitPolicy.Default,
            AiProviderQueuePolicy.Default,
            new AiProviderRetryPolicy(
                maxAttempts,
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(10),
                jitterRatio: 0m),
            AiProviderCircuitBreakerPolicy.Default));

    private static AiGatewayPolicy Policy(params AiProviderMetadata[] fallback) =>
        new(
            new[] { Primary }.Concat(fallback),
            hasAvailableBudget: true,
            maxOutputTokens: null,
            maxTimeout: null,
            allowedProcessingModes: null,
            authorizedTools: null,
            fallback: fallback);

    private static AiGatewayRequest Request(AiGatewayPolicy? policy = null) =>
        new(
            Organization,
            Position,
            Thread,
            Message,
            "Classify this bug.",
            provider: Primary,
            policy: policy);

    private static AiGatewayResponse Success(
        AiGatewayRequest request,
        AiCostMetadata? cost = null) =>
        AiGatewayResponse.Succeeded(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            "Done.",
            AiFinishReason.Stop,
            request.Provider,
            cost: cost);

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
            "Provider failed.",
            isRetryable,
            request.Provider));

    private sealed class FixedPolicyResolver(AiProviderResiliencePolicy policy)
        : IAiProviderResiliencePolicyResolver
    {
        public AiProviderResiliencePolicy Resolve(AiProviderMetadata? provider) => policy;
    }

    private sealed class ImmediateBackoff : IAiProviderRetryBackoff
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

    private sealed class OpenCircuitBreaker : IAiProviderCircuitBreaker
    {
        public AiProviderCircuitAdmission Acquire(AiGatewayRequest request) =>
            AiProviderCircuitAdmission.Rejected(
                AiGatewayResilienceErrorCatalog.CircuitOpen(request));
    }

    private sealed class RejectingAdmissionLimiter : IAiProviderAdmissionLimiter
    {
        public ValueTask<AiProviderAdmissionResult> AcquireAsync(
            AiGatewayRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AiProviderAdmissionResult.Rejected(
                AiGatewayResilienceErrorCatalog.GatewayOverloaded(request)));
    }

    private sealed class ScriptedProvider(
        Func<int, AiGatewayRequest, AiGatewayResponse> respond)
        : IAiGatewayProvider
    {
        private readonly List<AiGatewayRequest> _requests = [];

        public IReadOnlyList<AiGatewayRequest> Requests => _requests;

        public Task<AiGatewayResponse> CompleteAsync(
            AiGatewayRequest request,
            CancellationToken cancellationToken)
        {
            _requests.Add(request);
            return Task.FromResult(respond(_requests.Count, request));
        }
    }

    private sealed class RecordingAuditPublisher : IAiGatewayAuditPublisher
    {
        private readonly List<AiGatewayCostAuditEvent> _events = [];

        public IReadOnlyList<AiGatewayCostAuditEvent> Events => _events;

        public void Publish(AiGatewayCostAuditEvent auditEvent) => _events.Add(auditEvent);
    }

    private sealed class RecordingDetailedAuditPublisher : IAiGatewayDetailedAuditPublisher
    {
        private readonly List<AiGatewayAuditEnvelope> _envelopes = [];

        public IReadOnlyList<AiGatewayAuditEnvelope> Envelopes => _envelopes;

        public void Publish(AiGatewayAuditEnvelope envelope) => _envelopes.Add(envelope);
    }
}
